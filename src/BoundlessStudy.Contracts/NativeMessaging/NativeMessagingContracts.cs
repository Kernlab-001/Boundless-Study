using System.Buffers.Binary;
using System.Text.Json;

namespace BoundlessStudy.Contracts.NativeMessaging;

/// <summary>
/// Native Messaging contracts for Chrome and Edge Manifest V3 communication (ADR-0004; §7.3).
/// Messages are length-prefixed with a 4-byte little-endian length.
/// </summary>
public static class NativeMessagingProtocol
{
    public const string HostName = "com.boundlessstudy.bridge";
    public const string ExtensionId = "jjfgoeghdckpppcfjinhcccffmkmkibn";
    public const string AllowedOrigin = "chrome-extension://jjfgoeghdckpppcfjinhcccffmkmkibn/";
    public const int MaxMessageBytes = 1024 * 1024; // 1 MB

    // Message types
    public const string TypeHello = "hello";
    public const string TypeHeartbeat = "heartbeat";
    public const string TypeGetPolicy = "getPolicy";
    public const string TypeReportViolation = "reportViolation";
    public const string TypePolicyApplied = "policyApplied";
    public const string TypePolicy = "policy";
    public const string TypeHeartbeatAck = "heartbeatAck";
    public const string TypeHelloAck = "helloAck";
    public const string TypeError = "error";
    public const string TypePolicyAppliedAck = "policyAppliedAck";
    public const string TypeViolationAck = "violationAck";
}

public sealed record NativeInboundMessage(
    string Type,
    string? ExtensionId = null,
    string? Version = null,
    string? BrowserFamily = null,
    Guid? SessionId = null,
    string? Domain = null,
    DateTime? TimestampUtc = null,
    bool? IsApplied = null,
    string? ErrorCode = null);

public sealed record NativeOutboundMessage(
    string Type,
    bool Success,
    Guid? SessionId = null,
    IReadOnlyList<string>? BlockedDomains = null,
    IReadOnlyList<string>? AllowedDomains = null,
    DateTime? ExpiresAtUtc = null,
    string? ErrorMessage = null);

public static class NativeFrameCodec
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static async Task WriteMessageAsync(Stream stream, NativeOutboundMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(message);

        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, SerializerOptions);
        if (bytes.Length > NativeMessagingProtocol.MaxMessageBytes)
        {
            throw new InvalidDataException("Native messaging frame exceeds 1 MB maximum length.");
        }

        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, bytes.Length);
        await stream.WriteAsync(prefix, cancellationToken);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<NativeInboundMessage?> ReadMessageAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var prefix = new byte[4];
        var read = await stream.ReadAsync(prefix, cancellationToken);
        if (read == 0) return null;
        if (read < prefix.Length)
        {
            await stream.ReadExactlyAsync(prefix.AsMemory(read), cancellationToken);
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length is <= 0 or > NativeMessagingProtocol.MaxMessageBytes)
        {
            throw new InvalidDataException($"Native messaging frame length {length} is outside allowed range (1..{NativeMessagingProtocol.MaxMessageBytes}).");
        }

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);

        try
        {
            var message = JsonSerializer.Deserialize<NativeInboundMessage>(payload, SerializerOptions);
            if (message is null || string.IsNullOrWhiteSpace(message.Type))
            {
                throw new InvalidDataException("The Native Messaging payload must be a message object with a type.");
            }

            return message;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The Native Messaging payload is not valid JSON.", ex);
        }
    }
}
