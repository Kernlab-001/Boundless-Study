using System.Diagnostics;
using Boundless.Protection.Engine;

namespace Boundless.Protection.Engine.Tests;

public sealed class WindowsProcessIdentityVerifierTests
{
    private const string SubjectSidOne = "S-1-5-21-111111111-222222222-333333333-1001";
    private const string SubjectSidTwo = "S-1-5-21-111111111-222222222-333333333-1002";
    private const string SyntheticHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string SyntheticPath = "C:\\BPE-Synthetic\\reader.exe";
    private static readonly DateTimeOffset BaseStartUtc = new(2026, 11, 24, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset BaseNowUtc = BaseStartUtc.AddSeconds(5);

    [Fact]
    public async Task VerifyProcessAsync_CurrentLiveProcess_ReturnsVerifiedEvidence()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Assert.True(WindowsSessionIdentityVerifier.TryReadCurrentProcessTokenIdentity(out var osIdentity, out var error), error);
        using var current = Process.GetCurrentProcess();
        var startUtc = current.StartTime.ToUniversalTime();
        var session = VerifiedSession(osIdentity.UserSid);
        var claims = new CallerProcessIdentityClaims(
            current.Id,
            startUtc,
            osIdentity.UserSid,
            SyntheticPath,
            SyntheticHash);
        var verifier = new WindowsProcessIdentityVerifier();

        var result = await verifier.VerifyProcessAsync(claims, session);

        Assert.Equal(IdentityVerificationStatus.Verified, result.Status);
        Assert.Equal(IdentityEvidenceSourceTrust.OperatingSystemVerified, result.SourceTrust);
        Assert.True(result.IsTrusted);
        Assert.Equal("identity-verified", result.ReasonCode);
        Assert.NotNull(result.Evidence);
        Assert.Equal(current.Id, result.Evidence.ProcessId);
        Assert.Equal(startUtc, result.Evidence.ProcessStartUtc);
        Assert.Equal(TimeSpan.Zero, result.Evidence.ProcessStartUtc.Offset);
        Assert.Equal(TimeSpan.Zero, result.Evidence.ObservedAtUtc.Offset);
        Assert.True(result.Evidence.ProcessStartUtc <= result.Evidence.ObservedAtUtc);
        Assert.Equal(osIdentity.UserSid, result.Evidence.SubjectSid);
        Assert.Equal(session.SessionId, result.Evidence.SessionId);
        Assert.Equal(session.Generation, result.Evidence.SessionGeneration);
    }

    [Fact]
    public async Task VerifyProcessAsync_CurrentProcessWithMismatchedStartTime_RejectsAsPidReuse()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Assert.True(WindowsSessionIdentityVerifier.TryReadCurrentProcessTokenIdentity(out var osIdentity, out var error), error);
        using var current = Process.GetCurrentProcess();
        var startUtc = current.StartTime.ToUniversalTime();
        var session = VerifiedSession(osIdentity.UserSid);
        var claims = new CallerProcessIdentityClaims(
            current.Id,
            startUtc.AddSeconds(-5),
            osIdentity.UserSid,
            SyntheticPath,
            SyntheticHash);

        var result = await new WindowsProcessIdentityVerifier().VerifyProcessAsync(claims, session);

        Assert.Equal(IdentityVerificationStatus.Rejected, result.Status);
        Assert.False(result.IsTrusted);
        Assert.Null(result.Evidence);
        Assert.Equal("process-pid-reused-or-start-time-mismatch", result.ReasonCode);
    }

    [Fact]
    public async Task VerifyProcessAsync_CurrentProcessWithMismatchedSid_RejectsOwnerMismatch()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Assert.True(WindowsSessionIdentityVerifier.TryReadCurrentProcessTokenIdentity(out var osIdentity, out var error), error);
        using var current = Process.GetCurrentProcess();
        var otherSid = string.Equals(osIdentity.UserSid, SubjectSidOne, StringComparison.Ordinal)
            ? SubjectSidTwo
            : SubjectSidOne;
        var session = VerifiedSession(otherSid);
        var claims = new CallerProcessIdentityClaims(
            current.Id,
            current.StartTime.ToUniversalTime(),
            otherSid,
            SyntheticPath,
            SyntheticHash);

        var result = await new WindowsProcessIdentityVerifier().VerifyProcessAsync(claims, session);

        Assert.Equal(IdentityVerificationStatus.Rejected, result.Status);
        Assert.False(result.IsTrusted);
        Assert.Null(result.Evidence);
        Assert.Equal("process-owner-sid-mismatch", result.ReasonCode);
    }

    [Fact]
    public async Task VerifyProcessAsync_SpawnedChildProcess_VerifiesWhileAliveAndFailsAfterExitAndDispose()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Assert.True(WindowsSessionIdentityVerifier.TryReadCurrentProcessTokenIdentity(out var osIdentity, out var error), error);
        var verifier = new WindowsProcessIdentityVerifier();
        var session = VerifiedSession(osIdentity.UserSid);

        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/Q /K",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        int childPid;
        DateTimeOffset childStartUtc;
        using (var child = Process.Start(startInfo)!)
        {
            try
            {
                childPid = child.Id;
                childStartUtc = child.StartTime.ToUniversalTime();
                var claims = new CallerProcessIdentityClaims(
                    childPid,
                    childStartUtc,
                    osIdentity.UserSid,
                    SyntheticPath,
                    SyntheticHash);

                var liveResult = await verifier.VerifyProcessAsync(claims, session);
                Assert.Equal(IdentityVerificationStatus.Verified, liveResult.Status);
                Assert.True(liveResult.IsTrusted);

                await child.StandardInput.WriteLineAsync("exit 259");
                await child.StandardInput.FlushAsync();
                child.StandardInput.Close();
                Assert.True(child.WaitForExit(5000));

                // Keeping 'child' undisposed retains an open OS handle to the exited process
                // (even with exit code 259 == STILL_ACTIVE), testing lpExitTime detection.
                var exitedResult = await verifier.VerifyProcessAsync(claims, session);
                Assert.Equal(IdentityVerificationStatus.Unavailable, exitedResult.Status);
                Assert.False(exitedResult.IsTrusted);
                Assert.Null(exitedResult.Evidence);
                Assert.Equal("process-already-exited", exitedResult.ReasonCode);
            }
            finally
            {
                if (!child.HasExited)
                    child.Kill(entireProcessTree: true);
            }
        }

        // After Process.Dispose closes our handle, Windows either reaps the PID
        // (process-not-found) or reports process-already-exited if another kernel/console reference lingers briefly.
        var reapedClaims = new CallerProcessIdentityClaims(
            childPid,
            childStartUtc,
            osIdentity.UserSid,
            SyntheticPath,
            SyntheticHash);
        var reapedResult = await verifier.VerifyProcessAsync(reapedClaims, session);
        Assert.Equal(IdentityVerificationStatus.Unavailable, reapedResult.Status);
        Assert.False(reapedResult.IsTrusted);
        Assert.True(
            reapedResult.ReasonCode is "process-not-found" or "process-already-exited",
            $"Unexpected reason code after dispose: {reapedResult.ReasonCode}");
    }

    [Fact]
    public async Task VerifyProcessAsync_WhenNotWindows_ReturnsUnsupported()
    {
        var probe = new FakeWindowsProcessProbe();
        var verifier = new WindowsProcessIdentityVerifier(probe, () => false, () => BaseNowUtc);
        var claims = ValidClaims(4242);

        var result = await verifier.VerifyProcessAsync(claims, VerifiedSession(SubjectSidOne));

        Assert.Equal(IdentityVerificationStatus.Unsupported, result.Status);
        Assert.False(result.IsTrusted);
        Assert.Null(result.Evidence);
        Assert.Equal("platform-unsupported-windows-identity-required", result.ReasonCode);
        Assert.Equal(0, probe.OpenProcessCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-42)]
    public async Task VerifyProcessAsync_InvalidProcessId_ReturnsRejected(int processId)
    {
        var probe = new FakeWindowsProcessProbe();
        var verifier = new WindowsProcessIdentityVerifier(probe, () => true, () => BaseNowUtc);
        var claims = ValidClaims(processId);

        var result = await verifier.VerifyProcessAsync(claims, VerifiedSession(SubjectSidOne));

        Assert.Equal(IdentityVerificationStatus.Rejected, result.Status);
        Assert.False(result.IsTrusted);
        Assert.Null(result.Evidence);
        Assert.Equal("invalid-process-id", result.ReasonCode);
        Assert.Equal(0, probe.OpenProcessCalls);
    }

    [Theory]
    [InlineData(WindowsSecurityApis.ERROR_ACCESS_DENIED, "process-access-denied")]
    [InlineData(WindowsSecurityApis.ERROR_INVALID_PARAMETER, "process-not-found")]
    [InlineData(1450, "process-open-failed: 1450")]
    public async Task VerifyProcessAsync_OpenProcessFailure_ReturnsExpectedUnavailable(int win32Error, string expectedReason)
    {
        var probe = new FakeWindowsProcessProbe
        {
            OpenProcessResult = false,
            OpenProcessError = win32Error
        };
        var verifier = new WindowsProcessIdentityVerifier(probe, () => true, () => BaseNowUtc);

        var result = await verifier.VerifyProcessAsync(ValidClaims(4242), VerifiedSession(SubjectSidOne));

        Assert.Equal(IdentityVerificationStatus.Unavailable, result.Status);
        Assert.False(result.IsTrusted);
        Assert.Null(result.Evidence);
        Assert.Equal(expectedReason, result.ReasonCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5_000)]
    [InlineData(-5_000)]
    [InlineData(10_000)]
    [InlineData(-10_000)]
    public async Task VerifyProcessAsync_StartTimeWithinOneMillisecondTolerance_Succeeds(long deltaTicks)
    {
        var probe = new FakeWindowsProcessProbe();
        var verifier = new WindowsProcessIdentityVerifier(probe, () => true, () => BaseNowUtc);
        var claims = ValidClaims(4242, startUtc: BaseStartUtc.AddTicks(deltaTicks));

        var result = await verifier.VerifyProcessAsync(claims, VerifiedSession(SubjectSidOne));

        Assert.Equal(IdentityVerificationStatus.Verified, result.Status);
        Assert.True(result.IsTrusted);
        Assert.Equal(BaseStartUtc, result.Evidence!.ProcessStartUtc);
        Assert.Equal(2, probe.GetProcessTimesCalls);
        Assert.Equal(2, probe.GetExitCodeCalls);
        Assert.True(probe.ProcessHandleDisposed);
        Assert.True(probe.TokenHandleDisposed);
    }

    [Theory]
    [InlineData(10_001)]
    [InlineData(-10_001)]
    [InlineData(20_000)]
    [InlineData(-50_000_000)]
    public async Task VerifyProcessAsync_StartTimeDifferenceExceedsOneMillisecond_RejectsAsPidReuse(long deltaTicks)
    {
        var probe = new FakeWindowsProcessProbe();
        var verifier = new WindowsProcessIdentityVerifier(probe, () => true, () => BaseNowUtc);
        var claims = ValidClaims(4242, startUtc: BaseStartUtc.AddTicks(deltaTicks));

        var result = await verifier.VerifyProcessAsync(claims, VerifiedSession(SubjectSidOne));

        Assert.Equal(IdentityVerificationStatus.Rejected, result.Status);
        Assert.False(result.IsTrusted);
        Assert.Null(result.Evidence);
        Assert.Equal("process-pid-reused-or-start-time-mismatch", result.ReasonCode);
        Assert.True(probe.ProcessHandleDisposed);
    }

    [Fact]
    public async Task VerifyProcessAsync_NonZeroExitCode_ReturnsUnavailableAlreadyExited()
    {
        var probe = new FakeWindowsProcessProbe { ExitCodeSequence = [1u] };
        var verifier = new WindowsProcessIdentityVerifier(probe, () => true, () => BaseNowUtc);

        var result = await verifier.VerifyProcessAsync(ValidClaims(4242), VerifiedSession(SubjectSidOne));

        Assert.Equal(IdentityVerificationStatus.Unavailable, result.Status);
        Assert.Equal("process-already-exited", result.ReasonCode);
        Assert.True(probe.ProcessHandleDisposed);
    }

    [Fact]
    public async Task VerifyProcessAsync_WaitSignaledObjectZero_ReturnsUnavailableAlreadyExited()
    {
        var probe = new FakeWindowsProcessProbe
        {
            WaitSequence = [WindowsSecurityApis.WAIT_OBJECT_0]
        };
        var verifier = new WindowsProcessIdentityVerifier(probe, () => true, () => BaseNowUtc);

        var result = await verifier.VerifyProcessAsync(ValidClaims(4242), VerifiedSession(SubjectSidOne));

        Assert.Equal(IdentityVerificationStatus.Unavailable, result.Status);
        Assert.Equal("process-already-exited", result.ReasonCode);
        Assert.True(probe.ProcessHandleDisposed);
    }

    [Fact]
    public async Task VerifyProcessAsync_WaitFailedWithAccessDeniedOnLimitedHandle_StillVerifiesWhenAlive()
    {
        var probe = new FakeWindowsProcessProbe
        {
            WaitSequence = [WindowsSecurityApis.WAIT_FAILED, WindowsSecurityApis.WAIT_FAILED],
            WaitError = WindowsSecurityApis.ERROR_ACCESS_DENIED
        };
        var verifier = new WindowsProcessIdentityVerifier(probe, () => true, () => BaseNowUtc);

        var result = await verifier.VerifyProcessAsync(ValidClaims(4242), VerifiedSession(SubjectSidOne));

        Assert.Equal(IdentityVerificationStatus.Verified, result.Status);
        Assert.True(result.IsTrusted);
    }

    [Fact]
    public async Task VerifyProcessAsync_WaitFailedWithUnexpectedError_ReturnsUnavailable()
    {
        var probe = new FakeWindowsProcessProbe
        {
            WaitSequence = [WindowsSecurityApis.WAIT_FAILED],
            WaitError = 6
        };
        var verifier = new WindowsProcessIdentityVerifier(probe, () => true, () => BaseNowUtc);

        var result = await verifier.VerifyProcessAsync(ValidClaims(4242), VerifiedSession(SubjectSidOne));

        Assert.Equal(IdentityVerificationStatus.Unavailable, result.Status);
        Assert.Equal("process-wait-failed: 6", result.ReasonCode);
    }

    [Fact]
    public async Task VerifyProcessAsync_OpenProcessTokenAccessDenied_ReturnsUnavailable()
    {
        var probe = new FakeWindowsProcessProbe
        {
            OpenTokenResult = false,
            OpenTokenError = WindowsSecurityApis.ERROR_ACCESS_DENIED
        };
        var verifier = new WindowsProcessIdentityVerifier(probe, () => true, () => BaseNowUtc);

        var result = await verifier.VerifyProcessAsync(ValidClaims(4242), VerifiedSession(SubjectSidOne));

        Assert.Equal(IdentityVerificationStatus.Unavailable, result.Status);
        Assert.Equal("process-token-access-denied", result.ReasonCode);
        Assert.True(probe.ProcessHandleDisposed);
    }

    [Fact]
    public async Task VerifyProcessAsync_TokenUserSidDiffersFromSessionSid_Rejects()
    {
        var probe = new FakeWindowsProcessProbe { TokenUserSid = SubjectSidTwo };
        var verifier = new WindowsProcessIdentityVerifier(probe, () => true, () => BaseNowUtc);

        var result = await verifier.VerifyProcessAsync(
            ValidClaims(4242, sid: SubjectSidOne),
            VerifiedSession(SubjectSidOne));

        Assert.Equal(IdentityVerificationStatus.Rejected, result.Status);
        Assert.Equal("process-owner-sid-mismatch", result.ReasonCode);
        Assert.True(probe.TokenHandleDisposed);
        Assert.True(probe.ProcessHandleDisposed);
    }

    [Fact]
    public async Task VerifyProcessAsync_TokenUserSidDiffersFromClaimSid_Rejects()
    {
        var probe = new FakeWindowsProcessProbe { TokenUserSid = SubjectSidOne };
        var verifier = new WindowsProcessIdentityVerifier(probe, () => true, () => BaseNowUtc);

        var result = await verifier.VerifyProcessAsync(
            ValidClaims(4242, sid: SubjectSidTwo),
            VerifiedSession(SubjectSidOne));

        Assert.Equal(IdentityVerificationStatus.Rejected, result.Status);
        Assert.Equal("process-owner-sid-mismatch", result.ReasonCode);
    }

    [Fact]
    public async Task VerifyProcessAsync_ProcessIdSessionDiffersFromTokenSession_Rejects()
    {
        var probe = new FakeWindowsProcessProbe
        {
            TokenSessionId = 1,
            PidSessionId = 2
        };
        var verifier = new WindowsProcessIdentityVerifier(probe, () => true, () => BaseNowUtc);

        var result = await verifier.VerifyProcessAsync(ValidClaims(4242), VerifiedSession(SubjectSidOne));

        Assert.Equal(IdentityVerificationStatus.Rejected, result.Status);
        Assert.Equal("process-pid-reused-or-session-mismatch", result.ReasonCode);
    }

    [Fact]
    public async Task VerifyProcessAsync_DoubleCheckDetectsExitBeforeEvidenceIssuance()
    {
        var probe = new FakeWindowsProcessProbe
        {
            ExitCodeSequence = [WindowsSecurityApis.STILL_ACTIVE, 0u]
        };
        var verifier = new WindowsProcessIdentityVerifier(probe, () => true, () => BaseNowUtc);

        var result = await verifier.VerifyProcessAsync(ValidClaims(4242), VerifiedSession(SubjectSidOne));

        Assert.Equal(IdentityVerificationStatus.Unavailable, result.Status);
        Assert.False(result.IsTrusted);
        Assert.Null(result.Evidence);
        Assert.Equal("process-already-exited", result.ReasonCode);
        Assert.Equal(2, probe.GetExitCodeCalls);
        Assert.True(probe.ProcessHandleDisposed);
    }

    [Fact]
    public async Task VerifyProcessAsync_DoubleCheckDetectsStartTimeChangeOnSameHandle()
    {
        var probe = new FakeWindowsProcessProbe
        {
            CreationTimeSequence = [BaseStartUtc, BaseStartUtc.AddSeconds(1)]
        };
        var verifier = new WindowsProcessIdentityVerifier(probe, () => true, () => BaseNowUtc);

        var result = await verifier.VerifyProcessAsync(ValidClaims(4242), VerifiedSession(SubjectSidOne));

        Assert.Equal(IdentityVerificationStatus.Rejected, result.Status);
        Assert.False(result.IsTrusted);
        Assert.Null(result.Evidence);
        Assert.Equal("process-pid-reused-or-start-time-mismatch", result.ReasonCode);
        Assert.Equal(2, probe.GetProcessTimesCalls);
    }

    [Fact]
    public async Task VerifyProcessAsync_DoubleCheckDetectsExitTimeOnSecondTimesQuery()
    {
        var probe = new FakeWindowsProcessProbe
        {
            HasExitTimeSequence = [false, true]
        };
        var verifier = new WindowsProcessIdentityVerifier(probe, () => true, () => BaseNowUtc);

        var result = await verifier.VerifyProcessAsync(ValidClaims(4242), VerifiedSession(SubjectSidOne));

        Assert.Equal(IdentityVerificationStatus.Unavailable, result.Status);
        Assert.Equal("process-already-exited", result.ReasonCode);
        Assert.Equal(2, probe.GetProcessTimesCalls);
    }

    [Fact]
    public async Task VerifyProcessAsync_DoubleCheckDetectsWaitSignalOnSecondCheck()
    {
        var probe = new FakeWindowsProcessProbe
        {
            WaitSequence = [WindowsSecurityApis.WAIT_TIMEOUT, WindowsSecurityApis.WAIT_OBJECT_0]
        };
        var verifier = new WindowsProcessIdentityVerifier(probe, () => true, () => BaseNowUtc);

        var result = await verifier.VerifyProcessAsync(ValidClaims(4242), VerifiedSession(SubjectSidOne));

        Assert.Equal(IdentityVerificationStatus.Unavailable, result.Status);
        Assert.Equal("process-already-exited", result.ReasonCode);
    }

    [Fact]
    public async Task VerifyProcessAsync_HonorsCancellationAndNullGuards()
    {
        var verifier = new WindowsProcessIdentityVerifier(new FakeWindowsProcessProbe(), () => true, () => BaseNowUtc);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            verifier.VerifyProcessAsync(null!, VerifiedSession(SubjectSidOne)));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            verifier.VerifyProcessAsync(ValidClaims(4242), null!));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            verifier.VerifyProcessAsync(ValidClaims(4242), VerifiedSession(SubjectSidOne), cts.Token));
    }

    private static CallerProcessIdentityClaims ValidClaims(
        int processId,
        DateTimeOffset? startUtc = null,
        string sid = SubjectSidOne) =>
        new(processId, startUtc ?? BaseStartUtc, sid, SyntheticPath, SyntheticHash);

    private static VerifiedSessionIdentity VerifiedSession(string sid) =>
        new(Guid.NewGuid(), 1, sid, "0x1", BaseStartUtc.AddMinutes(-10), BaseStartUtc.AddMinutes(-10), BaseStartUtc.AddHours(8));

    private sealed class FakeWindowsProcessProbe : WindowsProcessIdentityVerifier.IWindowsProcessProbe
    {
        public int OpenProcessCalls { get; private set; }
        public int GetProcessTimesCalls { get; private set; }
        public int GetExitCodeCalls { get; private set; }
        public int WaitCalls { get; private set; }
        public bool ProcessHandleDisposed { get; private set; }
        public bool TokenHandleDisposed { get; private set; }

        public bool OpenProcessResult { get; init; } = true;
        public int OpenProcessError { get; init; }
        public DateTimeOffset[] CreationTimeSequence { get; init; } = [BaseStartUtc, BaseStartUtc];
        public bool[] HasExitTimeSequence { get; init; } = [false, false];
        public uint[] ExitCodeSequence { get; init; } = [WindowsSecurityApis.STILL_ACTIVE, WindowsSecurityApis.STILL_ACTIVE];
        public uint[] WaitSequence { get; init; } = [WindowsSecurityApis.WAIT_TIMEOUT, WindowsSecurityApis.WAIT_TIMEOUT];
        public int WaitError { get; init; }
        public bool OpenTokenResult { get; init; } = true;
        public int OpenTokenError { get; init; }
        public string TokenUserSid { get; init; } = SubjectSidOne;
        public uint TokenSessionId { get; init; } = 1;
        public uint PidSessionId { get; init; } = 1;

        public bool TryOpenProcess(
            int processId,
            out WindowsProcessIdentityVerifier.IProcessHandle? handle,
            out int win32Error)
        {
            OpenProcessCalls++;
            if (!OpenProcessResult)
            {
                handle = null;
                win32Error = OpenProcessError;
                return false;
            }

            handle = new FakeHandle(() => ProcessHandleDisposed = true);
            win32Error = WindowsSecurityApis.ERROR_SUCCESS;
            return true;
        }

        public bool TryGetProcessTimes(
            WindowsProcessIdentityVerifier.IProcessHandle handle,
            out DateTimeOffset creationTimeUtc,
            out bool hasExitTime,
            out int win32Error)
        {
            var idx = Math.Min(GetProcessTimesCalls, CreationTimeSequence.Length - 1);
            var exitIdx = Math.Min(GetProcessTimesCalls, HasExitTimeSequence.Length - 1);
            GetProcessTimesCalls++;
            creationTimeUtc = CreationTimeSequence[idx];
            hasExitTime = HasExitTimeSequence[exitIdx];
            win32Error = WindowsSecurityApis.ERROR_SUCCESS;
            return true;
        }

        public bool TryGetExitCode(
            WindowsProcessIdentityVerifier.IProcessHandle handle,
            out uint exitCode,
            out int win32Error)
        {
            var idx = Math.Min(GetExitCodeCalls, ExitCodeSequence.Length - 1);
            GetExitCodeCalls++;
            exitCode = ExitCodeSequence[idx];
            win32Error = WindowsSecurityApis.ERROR_SUCCESS;
            return true;
        }

        public uint WaitForExitZero(WindowsProcessIdentityVerifier.IProcessHandle handle, out int win32Error)
        {
            var idx = Math.Min(WaitCalls, WaitSequence.Length - 1);
            WaitCalls++;
            var result = WaitSequence[idx];
            win32Error = result == WindowsSecurityApis.WAIT_FAILED
                ? WaitError
                : WindowsSecurityApis.ERROR_SUCCESS;
            return result;
        }

        public bool TryOpenProcessToken(
            WindowsProcessIdentityVerifier.IProcessHandle processHandle,
            out WindowsProcessIdentityVerifier.IProcessTokenHandle? tokenHandle,
            out int win32Error)
        {
            if (!OpenTokenResult)
            {
                tokenHandle = null;
                win32Error = OpenTokenError;
                return false;
            }

            tokenHandle = new FakeTokenHandle(() => TokenHandleDisposed = true);
            win32Error = WindowsSecurityApis.ERROR_SUCCESS;
            return true;
        }

        public bool TryGetTokenUserSid(
            WindowsProcessIdentityVerifier.IProcessTokenHandle tokenHandle,
            out string? sid,
            out int win32Error)
        {
            sid = TokenUserSid;
            win32Error = WindowsSecurityApis.ERROR_SUCCESS;
            return true;
        }

        public bool TryGetTokenSessionId(
            WindowsProcessIdentityVerifier.IProcessTokenHandle tokenHandle,
            out uint sessionId,
            out int win32Error)
        {
            sessionId = TokenSessionId;
            win32Error = WindowsSecurityApis.ERROR_SUCCESS;
            return true;
        }

        public bool TryProcessIdToSessionId(int processId, out uint sessionId, out int win32Error)
        {
            sessionId = PidSessionId;
            win32Error = WindowsSecurityApis.ERROR_SUCCESS;
            return true;
        }

        private sealed class FakeHandle(Action onDispose) : WindowsProcessIdentityVerifier.IProcessHandle
        {
            public void Dispose() => onDispose();
        }

        private sealed class FakeTokenHandle(Action onDispose) : WindowsProcessIdentityVerifier.IProcessTokenHandle
        {
            public void Dispose() => onDispose();
        }
    }
}
