using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using BoundlessStudy.Bridge;
using BoundlessStudy.Contracts.Ipc;
using BoundlessStudy.Contracts.NativeMessaging;

namespace BoundlessStudy.Architecture.Tests;

/// <summary>
/// Architecture and unit tests for Chrome and Edge Manifest V3 browser blocking,
/// Declarative Net Request (DNR) rule generation, Native Messaging frame codec,
/// manifest consistency, and the Native Messaging Host bridge (ADR-0004, BS-BRW-006).
/// </summary>
public sealed class BrowserBlockingTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    // ---------------------------------------------------------------------------
    // 1. DNR Rule Generation & Priority Tests (BS-BRW-006)
    // ---------------------------------------------------------------------------

    [Fact]
    public void DnrRules_AllowedDomainsPriority2_TakesPrecedenceOverBlockedDomainsPriority1()
    {
        // BS-BRW-006: Allowed domains must take precedence over blocked domains.
        // In Chromium Declarative Net Request, higher numerical priority takes precedence.
        Assert.True(BrowserRuleGenerator.AllowPriority > BrowserRuleGenerator.BlockPriority,
            "AllowPriority (2) must strictly exceed BlockPriority (1) to guarantee precedence.");
        Assert.Equal(1, BrowserRuleGenerator.BlockPriority);
        Assert.Equal(2, BrowserRuleGenerator.AllowPriority);
    }

    [Fact]
    public void DnrRules_GenerateRules_AssignsCorrectPrioritiesAndActions()
    {
        var blocked = new[] { "youtube.com", "reddit.com" };
        var allowed = new[] { "docs.google.com", "github.com" };

        var rules = BrowserRuleGenerator.GenerateRules(blocked, allowed, startingRuleId: 1000);

        Assert.Equal(4, rules.Count);

        // Verify blocked rules
        var blockRule1 = rules[0];
        Assert.Equal(1000, blockRule1.Id);
        Assert.Equal(BrowserRuleGenerator.BlockPriority, blockRule1.Priority);
        Assert.Equal("redirect", blockRule1.Action.Type);
        Assert.Equal("/blocked.html", blockRule1.Action.Redirect?.ExtensionPath);
        Assert.Equal("||youtube.com^", blockRule1.Condition.UrlFilter);
        Assert.Contains("main_frame", blockRule1.Condition.ResourceTypes);

        var blockRule2 = rules[1];
        Assert.Equal(1001, blockRule2.Id);
        Assert.Equal(BrowserRuleGenerator.BlockPriority, blockRule2.Priority);
        Assert.Equal("||reddit.com^", blockRule2.Condition.UrlFilter);

        // Verify allowed rules
        var allowRule1 = rules[2];
        Assert.Equal(1002, allowRule1.Id);
        Assert.Equal(BrowserRuleGenerator.AllowPriority, allowRule1.Priority);
        Assert.Equal("allow", allowRule1.Action.Type);
        Assert.Null(allowRule1.Action.Redirect);
        Assert.Equal("||docs.google.com^", allowRule1.Condition.UrlFilter);
        Assert.Contains("main_frame", allowRule1.Condition.ResourceTypes);
        Assert.Contains("sub_frame", allowRule1.Condition.ResourceTypes);

        var allowRule2 = rules[3];
        Assert.Equal(1003, allowRule2.Id);
        Assert.Equal(BrowserRuleGenerator.AllowPriority, allowRule2.Priority);
        Assert.Equal("||github.com^", allowRule2.Condition.UrlFilter);

        // All rule IDs must be unique and strictly positive
        var ids = rules.Select(r => r.Id).ToArray();
        Assert.Equal(ids.Length, ids.Distinct().Count());
        Assert.All(ids, id => Assert.True(id > 0));
    }

    [Fact]
    public void DnrRules_CreateBlockRule_CanCreatePlainBlockOrRedirect()
    {
        var redirectRule = BrowserRuleGenerator.CreateBlockRule(101, "twitter.com", "/blocked.html");
        Assert.Equal("redirect", redirectRule.Action.Type);
        Assert.Equal("/blocked.html", redirectRule.Action.Redirect?.ExtensionPath);

        var plainBlockRule = BrowserRuleGenerator.CreateBlockRule(102, "twitter.com", redirectPath: null);
        Assert.Equal("block", plainBlockRule.Action.Type);
        Assert.Null(plainBlockRule.Action.Redirect);
    }

    // ---------------------------------------------------------------------------
    // 2. Domain Normalization & ||domain^ urlFilter Format
    // ---------------------------------------------------------------------------

    [Theory]
    [InlineData("example.com", "example.com")]
    [InlineData("EXAMPLE.COM", "example.com")]
    [InlineData("https://www.youtube.com/watch?v=123", "youtube.com")]
    [InlineData("http://reddit.com/", "reddit.com")]
    [InlineData("https://docs.google.com/document/d/123", "docs.google.com")]
    [InlineData("http://user:pass@sub.domain.org:8080/path?query=1#hash", "sub.domain.org")]
    [InlineData("  www.facebook.com  ", "facebook.com")]
    [InlineData("netflix.com.", "netflix.com")]
    [InlineData("https://discord.com", "discord.com")]
    public void DomainNormalization_NormalizesToCanonicalFormat(string raw, string expected)
    {
        var normalized = BrowserRuleGenerator.NormalizeDomain(raw);
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("example.com", "||example.com^")]
    [InlineData("https://www.youtube.com/feed/subscriptions", "||youtube.com^")]
    [InlineData("HTTP://REDDIT.COM", "||reddit.com^")]
    [InlineData("github.com:443", "||github.com^")]
    [InlineData("  docs.google.com  ", "||docs.google.com^")]
    public void DomainNormalization_ToUrlFilter_ProducesChromiumDnrFormat(string raw, string expectedFilter)
    {
        var filter = BrowserRuleGenerator.ToUrlFilter(raw);
        Assert.Equal(expectedFilter, filter);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void DomainNormalization_EmptyOrNull_ReturnsEmpty(string? raw)
    {
        Assert.Equal(string.Empty, BrowserRuleGenerator.NormalizeDomain(raw));
        Assert.Equal(string.Empty, BrowserRuleGenerator.ToUrlFilter(raw));
    }

    // ---------------------------------------------------------------------------
    // 3. Native Messaging Frame Codec Tests (4-byte LE prefix & JSON)
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task NativeFrameCodec_Roundtrip_InboundMessages()
    {
        var testMessages = new[]
        {
            new NativeInboundMessage(NativeMessagingProtocol.TypeHello, "jjfgoeghdckpppcfjinhcccffmkmkibn", "0.1.0", "chrome"),
            new NativeInboundMessage(NativeMessagingProtocol.TypeHeartbeat, "jjfgoeghdckpppcfjinhcccffmkmkibn", BrowserFamily: "chrome", SessionId: Guid.NewGuid()),
            new NativeInboundMessage(NativeMessagingProtocol.TypeGetPolicy, "jjfgoeghdckpppcfjinhcccffmkmkibn", BrowserFamily: "chrome"),
            new NativeInboundMessage(NativeMessagingProtocol.TypeReportViolation, "jjfgoeghdckpppcfjinhcccffmkmkibn", BrowserFamily: "chrome", Domain: "distraction.com", SessionId: Guid.NewGuid(), TimestampUtc: DateTime.UtcNow),
            new NativeInboundMessage(NativeMessagingProtocol.TypePolicyApplied, "jjfgoeghdckpppcfjinhcccffmkmkibn", BrowserFamily: "edge", IsApplied: true)
        };

        foreach (var original in testMessages)
        {
            using var stream = new MemoryStream();

            // Simulate extension writing to host stdin
            var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(original, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var prefix = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(prefix, jsonBytes.Length);
            await stream.WriteAsync(prefix);
            await stream.WriteAsync(jsonBytes);

            stream.Position = 0;

            var read = await NativeFrameCodec.ReadMessageAsync(stream);
            Assert.NotNull(read);
            Assert.Equal(original.Type, read.Type);
            Assert.Equal(original.ExtensionId, read.ExtensionId);
            Assert.Equal(original.SessionId, read.SessionId);
            Assert.Equal(original.Domain, read.Domain);
        }
    }

    [Fact]
    public async Task NativeFrameCodec_Roundtrip_OutboundMessages()
    {
        var sessionId = Guid.NewGuid();
        var blocked = new[] { "youtube.com", "reddit.com" };
        var allowed = new[] { "docs.google.com" };
        var expiresAt = DateTime.UtcNow.AddMinutes(30);

        var testMessages = new[]
        {
            new NativeOutboundMessage(NativeMessagingProtocol.TypeHelloAck, Success: true),
            new NativeOutboundMessage(NativeMessagingProtocol.TypeHeartbeatAck, Success: true, SessionId: sessionId),
            new NativeOutboundMessage(NativeMessagingProtocol.TypePolicy, Success: true, SessionId: sessionId, BlockedDomains: blocked, AllowedDomains: allowed, ExpiresAtUtc: expiresAt),
            new NativeOutboundMessage(NativeMessagingProtocol.TypeError, Success: false, ErrorMessage: "Agent offline")
        };

        foreach (var original in testMessages)
        {
            using var stream = new MemoryStream();
            await NativeFrameCodec.WriteMessageAsync(stream, original);

            stream.Position = 0;

            // Verify 4-byte LE prefix
            var prefix = new byte[4];
            var bytesRead = await stream.ReadAsync(prefix);
            Assert.Equal(4, bytesRead);
            var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
            Assert.True(length > 0);
            Assert.Equal(stream.Length - 4, length);

            // Verify payload deserialization
            var payload = new byte[length];
            await stream.ReadExactlyAsync(payload);
            var deserialized = JsonSerializer.Deserialize<NativeOutboundMessage>(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.NotNull(deserialized);
            Assert.Equal(original.Type, deserialized.Type);
            Assert.Equal(original.Success, deserialized.Success);
            Assert.Equal(original.SessionId, deserialized.SessionId);
            Assert.Equal(original.ErrorMessage, deserialized.ErrorMessage);
            if (original.BlockedDomains is not null)
            {
                Assert.Equal(original.BlockedDomains, deserialized.BlockedDomains);
            }
        }
    }

    [Fact]
    public async Task NativeFrameCodec_RejectsZeroOrNegativeLength()
    {
        using var stream = new MemoryStream();
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, 0); // length = 0
        await stream.WriteAsync(prefix);
        stream.Position = 0;

        await Assert.ThrowsAsync<InvalidDataException>(() => NativeFrameCodec.ReadMessageAsync(stream));

        stream.SetLength(0);
        BinaryPrimitives.WriteInt32LittleEndian(prefix, -50); // length < 0
        await stream.WriteAsync(prefix);
        stream.Position = 0;

        await Assert.ThrowsAsync<InvalidDataException>(() => NativeFrameCodec.ReadMessageAsync(stream));
    }

    [Fact]
    public async Task NativeFrameCodec_RejectsLengthExceeding1MB()
    {
        using var stream = new MemoryStream();
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, NativeMessagingProtocol.MaxMessageBytes + 1);
        await stream.WriteAsync(prefix);
        stream.Position = 0;

        await Assert.ThrowsAsync<InvalidDataException>(() => NativeFrameCodec.ReadMessageAsync(stream));
    }

    [Fact]
    public async Task NativeFrameCodec_RejectsMalformedJson()
    {
        using var stream = new MemoryStream();
        var badJson = Encoding.UTF8.GetBytes("{ this is not valid json }");
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, badJson.Length);
        await stream.WriteAsync(prefix);
        await stream.WriteAsync(badJson);
        stream.Position = 0;

        await Assert.ThrowsAsync<InvalidDataException>(() => NativeFrameCodec.ReadMessageAsync(stream));
    }

    [Fact]
    public async Task NativeFrameCodec_ReturnsNullOnEof()
    {
        using var stream = new MemoryStream();
        var result = await NativeFrameCodec.ReadMessageAsync(stream);
        Assert.Null(result);
    }

    // ---------------------------------------------------------------------------
    // 4. Extension Manifest MV3 & Host Manifest Consistency Tests
    // ---------------------------------------------------------------------------

    [Fact]
    public void ExtensionManifest_IsValidManifestV3_WithRequiredPermissions()
    {
        var manifestPath = Path.Combine(RepoRoot, "src", "extension", "manifest.json");
        Assert.True(File.Exists(manifestPath), $"manifest.json not found at {manifestPath}");

        var json = File.ReadAllText(manifestPath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // MV3 requirement
        Assert.Equal(3, root.GetProperty("manifest_version").GetInt32());

        // Basic identity
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("name").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("version").GetString()));

        // Permissions
        var permissions = root.GetProperty("permissions").EnumerateArray()
            .Select(p => p.GetString()!)
            .ToArray();

        Assert.Contains("declarativeNetRequest", permissions);
        Assert.Contains("storage", permissions);
        Assert.Contains("alarms", permissions);
        Assert.Contains("nativeMessaging", permissions);

        // Service worker
        var bg = root.GetProperty("background");
        Assert.Equal("background.js", bg.GetProperty("service_worker").GetString());

        // Key field present for fixed extension ID
        Assert.True(root.TryGetProperty("key", out var keyElement));
        Assert.False(string.IsNullOrWhiteSpace(keyElement.GetString()));
        var accessibleResources = root.GetProperty("web_accessible_resources").EnumerateArray().Single()
            .GetProperty("resources").EnumerateArray().Select(value => value.GetString()).ToArray();
        Assert.Equal(new[] { "blocked.html" }, accessibleResources);
        Assert.DoesNotContain("webRequest", permissions);
        Assert.DoesNotContain("history", permissions);
    }

    [Fact]
    public void HostManifest_MatchesExtensionId_AndNativeMessagingProtocol()
    {
        var hostManifestPath = Path.Combine(RepoRoot, "src", "extension", "com.boundlessstudy.bridge.json");
        Assert.True(File.Exists(hostManifestPath), $"Host manifest not found at {hostManifestPath}");

        var json = File.ReadAllText(hostManifestPath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Protocol matching
        Assert.Equal(NativeMessagingProtocol.HostName, root.GetProperty("name").GetString());
        Assert.Equal("stdio", root.GetProperty("type").GetString());
        Assert.Equal("BoundlessStudy.Bridge.exe", Path.GetFileName(root.GetProperty("path").GetString()));

        // Allowed origins
        var allowedOrigins = root.GetProperty("allowed_origins").EnumerateArray()
            .Select(o => o.GetString()!)
            .ToArray();

        Assert.Contains(NativeMessagingProtocol.AllowedOrigin, allowedOrigins);

        // Origin contains the exact extension ID
        var matchingOrigin = allowedOrigins.First(o => o.Contains(NativeMessagingProtocol.ExtensionId));
        Assert.Equal($"chrome-extension://{NativeMessagingProtocol.ExtensionId}/", matchingOrigin);
    }

    [Fact]
    public void ExtensionFiles_BlockedHtmlAndJs_ExistAndAreAccessible()
    {
        var blockedHtmlPath = Path.Combine(RepoRoot, "src", "extension", "blocked.html");
        var blockedJsPath = Path.Combine(RepoRoot, "src", "extension", "blocked.js");
        var bgJsPath = Path.Combine(RepoRoot, "src", "extension", "background.js");

        Assert.True(File.Exists(blockedHtmlPath), "blocked.html must exist.");
        Assert.True(File.Exists(blockedJsPath), "blocked.js must exist.");
        Assert.True(File.Exists(bgJsPath), "background.js must exist.");

        var html = File.ReadAllText(blockedHtmlPath);
        Assert.Contains("Focus Session Active", html);
        Assert.Contains("blocked.js", html);

        var bg = File.ReadAllText(bgJsPath);
        Assert.Contains("com.boundlessstudy.bridge", bg);
        Assert.Contains("jjfgoeghdckpppcfjinhcccffmkmkibn", bg);
        Assert.Contains("declarativeNetRequest", bg);
    }

    // ---------------------------------------------------------------------------
    // 5. NativeMessagingHost Bridge Message Translation Tests
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task NativeMessagingHost_TranslatesHello_ToBridgeHello()
    {
        IpcRequest? capturedRequest = null;
        var host = new NativeMessagingHost(
            stdin: Stream.Null,
            stdout: Stream.Null,
            ipcSender: (req, _) =>
            {
                capturedRequest = req;
                return Task.FromResult(IpcResponse.Ok(req.RequestId, req.CorrelationId));
            });

        var inbound = new NativeInboundMessage(
            Type: NativeMessagingProtocol.TypeHello,
            ExtensionId: NativeMessagingProtocol.ExtensionId,
            Version: "0.1.0",
            BrowserFamily: "chrome");

        var response = await host.ProcessMessageAsync(inbound);

        Assert.NotNull(capturedRequest);
        Assert.Equal(IpcProtocol.BridgeHelloOperation, capturedRequest.Operation);
        Assert.NotNull(capturedRequest.PayloadJson);
        Assert.Contains("jjfgoeghdckpppcfjinhcccffmkmkibn", capturedRequest.PayloadJson);

        Assert.Equal(NativeMessagingProtocol.TypeHelloAck, response.Type);
        Assert.True(response.Success);
        Assert.Null(response.ErrorMessage);
    }

    [Fact]
    public async Task NativeMessagingHost_TranslatesHeartbeat_ToBridgeHeartbeat()
    {
        var testSessionId = Guid.NewGuid();
        var agentSessionId = Guid.NewGuid();
        IpcRequest? capturedRequest = null;

        var host = new NativeMessagingHost(
            stdin: Stream.Null,
            stdout: Stream.Null,
            ipcSender: (req, _) =>
            {
                capturedRequest = req;
                var payload = JsonSerializer.Serialize(new BridgeHeartbeatResponse(agentSessionId), new JsonSerializerOptions(JsonSerializerDefaults.Web));
                return Task.FromResult(IpcResponse.Ok(req.RequestId, req.CorrelationId, payload));
            });

        var inbound = new NativeInboundMessage(
            Type: NativeMessagingProtocol.TypeHeartbeat,
            ExtensionId: NativeMessagingProtocol.ExtensionId,
            BrowserFamily: "edge",
            SessionId: testSessionId);

        var response = await host.ProcessMessageAsync(inbound);

        Assert.NotNull(capturedRequest);
        Assert.Equal(IpcProtocol.BridgeHeartbeatOperation, capturedRequest.Operation);
        Assert.Equal(NativeMessagingProtocol.TypeHeartbeatAck, response.Type);
        Assert.True(response.Success);
        Assert.Equal(agentSessionId, response.SessionId);
        Assert.Contains("\"browserFamily\":\"edge\"", capturedRequest.PayloadJson);
    }

    [Fact]
    public async Task NativeMessagingHost_TranslatesGetPolicy_ReturnsPolicyWithDomains()
    {
        var testSessionId = Guid.NewGuid();
        var blocked = new[] { "youtube.com", "reddit.com" };
        var allowed = new[] { "docs.google.com" };
        var expiresAt = DateTime.UtcNow.AddMinutes(25);

        var policyDto = new BrowserPolicyDto(
            FocusSessionId: testSessionId,
            IsActive: true,
            ExpiresAtUtc: expiresAt,
            BlockedDomains: blocked,
            AllowedDomains: allowed);

        var host = new NativeMessagingHost(
            stdin: Stream.Null,
            stdout: Stream.Null,
            ipcSender: (req, _) =>
            {
                var payload = JsonSerializer.Serialize(policyDto, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                return Task.FromResult(IpcResponse.Ok(req.RequestId, req.CorrelationId, payload));
            });

        var inbound = new NativeInboundMessage(Type: NativeMessagingProtocol.TypeGetPolicy, ExtensionId: NativeMessagingProtocol.ExtensionId, BrowserFamily: "chrome");
        var response = await host.ProcessMessageAsync(inbound);

        Assert.Equal(NativeMessagingProtocol.TypePolicy, response.Type);
        Assert.True(response.Success);
        Assert.Equal(testSessionId, response.SessionId);
        Assert.Equal(blocked, response.BlockedDomains);
        Assert.Equal(allowed, response.AllowedDomains);
        Assert.Equal(expiresAt, response.ExpiresAtUtc);
    }

    [Fact]
    public async Task NativeMessagingHost_TranslatesReportViolation_ToBridgeReportViolation()
    {
        var testSessionId = Guid.NewGuid();
        IpcRequest? capturedRequest = null;

        var host = new NativeMessagingHost(
            stdin: Stream.Null,
            stdout: Stream.Null,
            ipcSender: (req, _) =>
            {
                capturedRequest = req;
                return Task.FromResult(IpcResponse.Ok(req.RequestId, req.CorrelationId));
            });

        var inbound = new NativeInboundMessage(
            Type: NativeMessagingProtocol.TypeReportViolation,
            ExtensionId: NativeMessagingProtocol.ExtensionId,
            BrowserFamily: "chrome",
            SessionId: testSessionId,
            Domain: "facebook.com",
            TimestampUtc: DateTime.UtcNow);

        var response = await host.ProcessMessageAsync(inbound);

        Assert.NotNull(capturedRequest);
        Assert.Equal(IpcProtocol.BridgeReportViolationOperation, capturedRequest.Operation);
        Assert.Contains("facebook.com", capturedRequest.PayloadJson);
        Assert.Equal(NativeMessagingProtocol.TypeViolationAck, response.Type);
        Assert.True(response.Success);
    }

    [Fact]
    public async Task NativeMessagingHost_UnknownMessageType_ReturnsError()
    {
        var host = new NativeMessagingHost(
            stdin: Stream.Null,
            stdout: Stream.Null,
            ipcSender: (req, _) => Task.FromResult(IpcResponse.Ok(req.RequestId, req.CorrelationId)));

        var inbound = new NativeInboundMessage(Type: "unknownOperation", ExtensionId: NativeMessagingProtocol.ExtensionId, BrowserFamily: "chrome");
        var response = await host.ProcessMessageAsync(inbound);

        Assert.Equal(NativeMessagingProtocol.TypeError, response.Type);
        Assert.False(response.Success);
        Assert.Contains("Unsupported message type", response.ErrorMessage);
    }

    [Fact]
    public async Task NativeMessagingHost_WhenAgentFails_ReturnsGracefulFailure()
    {
        var host = new NativeMessagingHost(
            stdin: Stream.Null,
            stdout: Stream.Null,
            ipcSender: (req, _) => Task.FromResult(IpcResponse.Fail(
                req.RequestId,
                req.CorrelationId,
                IpcErrors.AgentUnavailable,
                "Agent is currently offline.")));

        var inbound = new NativeInboundMessage(Type: NativeMessagingProtocol.TypeGetPolicy, ExtensionId: NativeMessagingProtocol.ExtensionId, BrowserFamily: "chrome");
        var response = await host.ProcessMessageAsync(inbound);

        Assert.Equal(NativeMessagingProtocol.TypePolicy, response.Type);
        Assert.False(response.Success);
        Assert.Contains("Failed to retrieve browser policy", response.ErrorMessage);
    }

    [Fact]
    public async Task NativeMessagingHost_ReportsActualPolicyApplication_WithBrowserAndSession()
    {
        var sessionId = Guid.NewGuid();
        IpcRequest? captured = null;
        var host = new NativeMessagingHost(
            Stream.Null,
            Stream.Null,
            (request, _) =>
            {
                captured = request;
                return Task.FromResult(IpcResponse.Ok(request.RequestId, request.CorrelationId));
            });

        var response = await host.ProcessMessageAsync(new NativeInboundMessage(
            Type: NativeMessagingProtocol.TypePolicyApplied,
            ExtensionId: NativeMessagingProtocol.ExtensionId,
            BrowserFamily: "edge",
            SessionId: sessionId,
            IsApplied: false,
            ErrorCode: "DNR_UPDATE_FAILED"));

        Assert.NotNull(captured);
        Assert.Equal(IpcProtocol.BridgePolicyAppliedOperation, captured.Operation);
        var status = JsonSerializer.Deserialize<BridgePolicyAppliedRequest>(captured.PayloadJson!, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(status);
        Assert.Equal(NativeMessagingProtocol.ExtensionId, status.ExtensionId);
        Assert.Equal("edge", status.BrowserFamily);
        Assert.Equal(sessionId, status.FocusSessionId);
        Assert.False(status.IsApplied);
        Assert.Equal("DNR_UPDATE_FAILED", status.ErrorCode);
        Assert.Equal(NativeMessagingProtocol.TypePolicyAppliedAck, response.Type);
        Assert.True(response.Success);
    }

    [Fact]
    public async Task NativeMessagingHost_RejectsWrongExtensionOrUnsupportedBrowser()
    {
        var host = new NativeMessagingHost(Stream.Null, Stream.Null,
            (request, _) => Task.FromResult(IpcResponse.Ok(request.RequestId, request.CorrelationId)));

        var wrongExtension = await host.ProcessMessageAsync(new NativeInboundMessage(
            NativeMessagingProtocol.TypeHello, "wrong-extension", "0.1", "chrome"));
        var unsupportedBrowser = await host.ProcessMessageAsync(new NativeInboundMessage(
            NativeMessagingProtocol.TypeHello, NativeMessagingProtocol.ExtensionId, "0.1", "firefox"));

        Assert.False(wrongExtension.Success);
        Assert.False(unsupportedBrowser.Success);
        Assert.Equal(NativeMessagingProtocol.TypeError, wrongExtension.Type);
        Assert.Equal(NativeMessagingProtocol.TypeError, unsupportedBrowser.Type);
    }

    [Fact]
    public async Task NativeMessagingHost_RunAsync_EndToEndStream()
    {
        using var stdin = new MemoryStream();
        using var stdout = new MemoryStream();

        // Write hello and getPolicy inbound messages
        var helloMsg = new NativeInboundMessage(NativeMessagingProtocol.TypeHello, NativeMessagingProtocol.ExtensionId, "0.1.0", "chrome");
        var json1 = JsonSerializer.SerializeToUtf8Bytes(helloMsg, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var p1 = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(p1, json1.Length);
        await stdin.WriteAsync(p1);
        await stdin.WriteAsync(json1);

        var policyMsg = new NativeInboundMessage(NativeMessagingProtocol.TypeGetPolicy, NativeMessagingProtocol.ExtensionId, BrowserFamily: "chrome");
        var json2 = JsonSerializer.SerializeToUtf8Bytes(policyMsg, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var p2 = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(p2, json2.Length);
        await stdin.WriteAsync(p2);
        await stdin.WriteAsync(json2);

        stdin.Position = 0;

        var host = new NativeMessagingHost(
            stdin: stdin,
            stdout: stdout,
            ipcSender: (req, _) => Task.FromResult(IpcResponse.Ok(req.RequestId, req.CorrelationId)));

        await host.RunAsync();

        stdout.Position = 0;

        var out1 = await ReadOutboundMessageAsync(stdout);
        Assert.NotNull(out1);
        Assert.Equal(NativeMessagingProtocol.TypeHelloAck, out1.Type);
        Assert.True(out1.Success);

        var out2 = await ReadOutboundMessageAsync(stdout);
        Assert.NotNull(out2);
        Assert.Equal(NativeMessagingProtocol.TypePolicy, out2.Type);
    }

    private static async Task<NativeOutboundMessage?> ReadOutboundMessageAsync(Stream stream)
    {
        var prefix = new byte[4];
        var read = await stream.ReadAsync(prefix);
        if (read < 4) return null;
        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload);
        return JsonSerializer.Deserialize<NativeOutboundMessage>(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    // ---------------------------------------------------------------------------
    // Helper
    // ---------------------------------------------------------------------------

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "BoundlessStudy.slnx"))
                || File.Exists(Path.Combine(dir.FullName, "BoundlessStudy.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent!;
        }

        throw new InvalidOperationException("Could not locate the Boundless Study solution file.");
    }
}
