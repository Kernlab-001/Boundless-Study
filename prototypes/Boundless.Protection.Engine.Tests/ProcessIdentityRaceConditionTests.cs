using System.Diagnostics;
using Boundless.Protection.Engine;

namespace Boundless.Protection.Engine.Tests;

public sealed class ProcessIdentityRaceConditionTests
{
    private const string SubjectSidAlice = "S-1-5-21-111111111-222222222-333333333-1001";
    private const string SubjectSidBob = "S-1-5-21-111111111-222222222-333333333-1002";
    private const string SystemSid = "S-1-5-18";
    private const string SyntheticHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string SyntheticPath = @"C:\BPE-Synthetic\worker.exe";
    private static readonly DateTimeOffset BaseStartUtc = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset BaseNowUtc = BaseStartUtc.AddSeconds(10);

    // -------------------------------------------------------------------------
    // 1. PID-reuse protection
    // -------------------------------------------------------------------------

    [Fact]
    public async Task PidReuse_LiveProcessWithStaleChildStartTime_RejectsHijackAttempt()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Assert.True(WindowsSessionIdentityVerifier.TryReadCurrentProcessTokenIdentity(out var osIdentity, out var error), error);
        var verifier = new WindowsProcessIdentityVerifier();
        var session = VerifiedSession(osIdentity.UserSid);

        int oldChildPid;
        DateTimeOffset oldChildStartUtc;

        using (var oldChild = StartShortLivedChildProcess("/c exit 0"))
        {
            oldChildPid = oldChild.Id;
            oldChildStartUtc = oldChild.StartTime.ToUniversalTime();
            Assert.True(oldChild.WaitForExit(5000));
        }

        // Wait briefly if needed so Windows finishes reaping the exited child PID.
        await Task.Delay(50);

        // Even if an attacker or caller replays the old start time against a live PID
        // (e.g. a newly spawned process or current process that happens to hold a PID),
        // start-time binding must reject the mismatch.
        using var replacementChild = StartShortLivedChildProcess("/Q /K");
        try
        {
            var hijackedClaims = new CallerProcessIdentityClaims(
                replacementChild.Id,
                oldChildStartUtc,
                osIdentity.UserSid,
                SyntheticPath,
                SyntheticHash);

            var hijackResult = await verifier.VerifyProcessAsync(hijackedClaims, session);
            Assert.Equal(IdentityVerificationStatus.Rejected, hijackResult.Status);
            Assert.False(hijackResult.IsTrusted);
            Assert.Null(hijackResult.Evidence);
            Assert.Equal("process-pid-reused-or-start-time-mismatch", hijackResult.ReasonCode);

            // Also verify the exited PID with the new start time fails closed.
            if (oldChildPid != replacementChild.Id)
            {
                var stalePidWithNewStart = new CallerProcessIdentityClaims(
                    oldChildPid,
                    replacementChild.StartTime.ToUniversalTime(),
                    osIdentity.UserSid,
                    SyntheticPath,
                    SyntheticHash);

                var staleResult = await verifier.VerifyProcessAsync(stalePidWithNewStart, session);
                Assert.False(staleResult.IsTrusted);
                Assert.Null(staleResult.Evidence);
                Assert.True(
                    staleResult.Status is IdentityVerificationStatus.Unavailable or IdentityVerificationStatus.Rejected,
                    $"Unexpected status for stale PID: {staleResult.Status} ({staleResult.ReasonCode})");
            }
        }
        finally
        {
            if (!replacementChild.HasExited)
                replacementChild.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task PidReuse_SimulatedPidRecycledAfterExitWithinTwoMilliseconds_RejectsOldStartTime()
    {
        // Process A started at BaseStartUtc on PID 9001 and exited.
        // Process B immediately reused PID 9001 at BaseStartUtc + 2ms under the exact same user SID.
        var recycledProcessStartUtc = BaseStartUtc.AddMilliseconds(2);
        var probe = new ScriptedWindowsProcessProbe
        {
            CreationTimeSequence = [recycledProcessStartUtc, recycledProcessStartUtc]
        };
        var verifier = new WindowsProcessIdentityVerifier(probe, () => true, () => BaseNowUtc);

        var callerClaimsForOldProcess = ValidClaims(9001, startUtc: BaseStartUtc, sid: SubjectSidAlice);
        var result = await verifier.VerifyProcessAsync(callerClaimsForOldProcess, VerifiedSession(SubjectSidAlice));

        Assert.Equal(IdentityVerificationStatus.Rejected, result.Status);
        Assert.False(result.IsTrusted);
        Assert.Null(result.Evidence);
        Assert.Equal("process-pid-reused-or-start-time-mismatch", result.ReasonCode);
        Assert.True(probe.AllHandlesDisposed);
    }

    [Fact]
    public async Task PidReuse_CallerClaimsNewStartTimeAgainstStillLivingOldProcess_RejectsMismatch()
    {
        var probe = new ScriptedWindowsProcessProbe
        {
            CreationTimeSequence = [BaseStartUtc, BaseStartUtc]
        };
        var verifier = new WindowsProcessIdentityVerifier(probe, () => true, () => BaseNowUtc);

        var callerClaimsForNewProcess = ValidClaims(9001, startUtc: BaseStartUtc.AddSeconds(3), sid: SubjectSidAlice);
        var result = await verifier.VerifyProcessAsync(callerClaimsForNewProcess, VerifiedSession(SubjectSidAlice));

        Assert.Equal(IdentityVerificationStatus.Rejected, result.Status);
        Assert.False(result.IsTrusted);
        Assert.Null(result.Evidence);
        Assert.Equal("process-pid-reused-or-start-time-mismatch", result.ReasonCode);
        Assert.True(probe.AllHandlesDisposed);
    }

    [Fact]
    public async Task PidReuse_PidReassignedAcrossTerminalSessionsWithinTolerance_RejectsSessionMismatch()
    {
        var probe = new ScriptedWindowsProcessProbe
        {
            CreationTimeSequence = [BaseStartUtc, BaseStartUtc],
            TokenSessionId = 1,
            PidSessionId = 2
        };
        var verifier = new WindowsProcessIdentityVerifier(probe, () => true, () => BaseNowUtc);

        var result = await verifier.VerifyProcessAsync(
            ValidClaims(9001, startUtc: BaseStartUtc, sid: SubjectSidAlice),
            VerifiedSession(SubjectSidAlice));

        Assert.Equal(IdentityVerificationStatus.Rejected, result.Status);
        Assert.False(result.IsTrusted);
        Assert.Null(result.Evidence);
        Assert.Equal("process-pid-reused-or-session-mismatch", result.ReasonCode);
        Assert.True(probe.AllHandlesDisposed);
    }

    // -------------------------------------------------------------------------
    // 2. Process exit during inspection
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ProcessExit_ExitsBetweenOpenProcessAndFirstTimesCheck_FailsClosed()
    {
        var probe = new ScriptedWindowsProcessProbe
        {
            HasExitTimeSequence = [true]
        };
        var verifier = new WindowsProcessIdentityVerifier(probe, () => true, () => BaseNowUtc);

        var result = await verifier.VerifyProcessAsync(ValidClaims(5001), VerifiedSession(SubjectSidAlice));

        Assert.Equal(IdentityVerificationStatus.Unavailable, result.Status);
        Assert.False(result.IsTrusted);
        Assert.Null(result.Evidence);
        Assert.Equal("process-already-exited", result.ReasonCode);
        Assert.Equal(0, probe.OpenTokenCalls);
        Assert.True(probe.AllHandlesDisposed);
    }

    [Fact]
    public async Task ProcessExit_ExitsDuringTokenInspection_DoubleCheckFailsClosed()
    {
        // Simulate process alive during initial checks, then exiting right when OpenProcessToken runs.
        var processExited = false;
        var probe = new ScriptedWindowsProcessProbe
        {
            OnOpenProcessToken = () => processExited = true,
            GetProcessTimesOverride = callIndex =>
                (BaseStartUtc, hasExitTime: processExited, WindowsSecurityApis.ERROR_SUCCESS),
            GetExitCodeOverride = _ =>
                (processExited ? 0u : WindowsSecurityApis.STILL_ACTIVE, WindowsSecurityApis.ERROR_SUCCESS)
        };
        var verifier = new WindowsProcessIdentityVerifier(probe, () => true, () => BaseNowUtc);

        var result = await verifier.VerifyProcessAsync(ValidClaims(5002), VerifiedSession(SubjectSidAlice));

        Assert.Equal(IdentityVerificationStatus.Unavailable, result.Status);
        Assert.False(result.IsTrusted);
        Assert.Null(result.Evidence);
        Assert.Equal("process-already-exited", result.ReasonCode);
        Assert.Equal(2, probe.GetProcessTimesCalls);
        Assert.True(probe.AllHandlesDisposed);
    }

    [Fact]
    public async Task ProcessExit_ExitsWithCode259DuringDoubleCheck_DetectedViaExitTime()
    {
        // Even if a process exits with exit code 259 (STILL_ACTIVE), lpExitTime is non-zero
        // on the double-check and must still fail closed as process-already-exited.
        var processExited = false;
        var probe = new ScriptedWindowsProcessProbe
        {
            OnProcessIdToSessionId = () => processExited = true,
            GetProcessTimesOverride = _ =>
                (BaseStartUtc, hasExitTime: processExited, WindowsSecurityApis.ERROR_SUCCESS),
            GetExitCodeOverride = _ => (WindowsSecurityApis.STILL_ACTIVE, WindowsSecurityApis.ERROR_SUCCESS),
            WaitSequence = [WindowsSecurityApis.WAIT_FAILED, WindowsSecurityApis.WAIT_FAILED],
            WaitError = WindowsSecurityApis.ERROR_ACCESS_DENIED
        };
        var verifier = new WindowsProcessIdentityVerifier(probe, () => true, () => BaseNowUtc);

        var result = await verifier.VerifyProcessAsync(ValidClaims(5003), VerifiedSession(SubjectSidAlice));

        Assert.Equal(IdentityVerificationStatus.Unavailable, result.Status);
        Assert.False(result.IsTrusted);
        Assert.Null(result.Evidence);
        Assert.Equal("process-already-exited", result.ReasonCode);
        Assert.Equal(2, probe.GetProcessTimesCalls);
        Assert.True(probe.AllHandlesDisposed);
    }

    [Fact]
    public async Task ProcessExit_ExitsDuringDoubleCheckWaitSignal_FailsClosed()
    {
        var probe = new ScriptedWindowsProcessProbe
        {
            HasExitTimeSequence = [false, false],
            ExitCodeSequence = [WindowsSecurityApis.STILL_ACTIVE, WindowsSecurityApis.STILL_ACTIVE],
            WaitSequence = [WindowsSecurityApis.WAIT_TIMEOUT, WindowsSecurityApis.WAIT_OBJECT_0]
        };
        var verifier = new WindowsProcessIdentityVerifier(probe, () => true, () => BaseNowUtc);

        var result = await verifier.VerifyProcessAsync(ValidClaims(5004), VerifiedSession(SubjectSidAlice));

        Assert.Equal(IdentityVerificationStatus.Unavailable, result.Status);
        Assert.False(result.IsTrusted);
        Assert.Null(result.Evidence);
        Assert.Equal("process-already-exited", result.ReasonCode);
        Assert.Equal(2, probe.WaitCalls);
        Assert.True(probe.AllHandlesDisposed);
    }

    [Fact]
    public async Task ProcessExit_ConcurrentRealChildTerminationDuringVerification_AlwaysFailsClosedOrVerifiesCleanly()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Assert.True(WindowsSessionIdentityVerifier.TryReadCurrentProcessTokenIdentity(out var osIdentity, out var error), error);
        var verifier = new WindowsProcessIdentityVerifier();
        var session = VerifiedSession(osIdentity.UserSid);

        for (var iteration = 0; iteration < 5; iteration++)
        {
            using var child = StartShortLivedChildProcess("/Q /K");
            try
            {
                var childPid = child.Id;
                var childStartUtc = child.StartTime.ToUniversalTime();
                var claims = new CallerProcessIdentityClaims(
                    childPid,
                    childStartUtc,
                    osIdentity.UserSid,
                    SyntheticPath,
                    SyntheticHash);

                using var startGate = new ManualResetEventSlim(false);

                var killerTask = Task.Run(async () =>
                {
                    startGate.Wait();
                    await Task.Delay(iteration * 2);
                    if (!child.HasExited)
                        child.Kill(entireProcessTree: true);
                });

                var verifyTasks = Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
                {
                    startGate.Wait();
                    return await verifier.VerifyProcessAsync(claims, session);
                })).ToArray();

                startGate.Set();
                var results = await Task.WhenAll(verifyTasks);
                await killerTask;

                foreach (var result in results)
                {
                    if (result.IsTrusted)
                    {
                        Assert.Equal(IdentityVerificationStatus.Verified, result.Status);
                        Assert.NotNull(result.Evidence);
                        Assert.Equal(childPid, result.Evidence.ProcessId);
                        Assert.Equal(childStartUtc, result.Evidence.ProcessStartUtc);
                    }
                    else
                    {
                        Assert.Equal(IdentityVerificationStatus.Unavailable, result.Status);
                        Assert.Null(result.Evidence);
                        Assert.True(
                            result.ReasonCode is "process-already-exited" or "process-not-found" or "process-access-denied",
                            $"Unexpected fail-closed reason during exit race: {result.ReasonCode}");
                    }
                }
            }
            finally
            {
                if (!child.HasExited)
                    child.Kill(entireProcessTree: true);
            }
        }
    }

    // -------------------------------------------------------------------------
    // 3. Access-denied system processes
    // -------------------------------------------------------------------------

    [Fact]
    public async Task AccessDenied_SystemPid4_FailsClosedSafelyWithoutCrashing()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Assert.True(WindowsSessionIdentityVerifier.TryReadCurrentProcessTokenIdentity(out var osIdentity, out var error), error);
        var verifier = new WindowsProcessIdentityVerifier();
        var session = VerifiedSession(osIdentity.UserSid);

        // Do not call Process.GetProcessById(4).StartTime because reading StartTime itself
        // throws Win32Exception on protected system processes; pass a synthetic claim instead.
        var claims = new CallerProcessIdentityClaims(
            4,
            DateTimeOffset.UtcNow.AddHours(-1),
            osIdentity.UserSid,
            @"C:\Windows\System32\ntoskrnl.exe",
            SyntheticHash);

        var result = await verifier.VerifyProcessAsync(claims, session);

        Assert.False(result.IsTrusted);
        Assert.Null(result.Evidence);
        Assert.True(
            result.ReasonCode is "process-access-denied" or "process-token-access-denied"
                or "process-pid-reused-or-start-time-mismatch" or "process-owner-sid-mismatch",
            $"Unexpected reason code for PID 4: {result.Status} ({result.ReasonCode})");
    }

    [Fact]
    public async Task AccessDenied_ProtectedSystemProcesses_FailClosedSafelyWithoutCrashing()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Assert.True(WindowsSessionIdentityVerifier.TryReadCurrentProcessTokenIdentity(out var osIdentity, out var error), error);
        var verifier = new WindowsProcessIdentityVerifier();
        var session = VerifiedSession(osIdentity.UserSid);

        var targetNames = new[] { "csrss", "audiodg", "smss", "services", "lsass" };
        var inspectedAny = false;

        foreach (var name in targetNames)
        {
            var processes = Process.GetProcessesByName(name);
            try
            {
                foreach (var proc in processes)
                {
                    inspectedAny = true;
                    var claims = new CallerProcessIdentityClaims(
                        proc.Id,
                        DateTimeOffset.UtcNow.AddHours(-1),
                        osIdentity.UserSid,
                        $@"C:\Windows\System32\{name}.exe",
                        SyntheticHash);

                    var result = await verifier.VerifyProcessAsync(claims, session);

                    Assert.False(result.IsTrusted);
                    Assert.Null(result.Evidence);
                    Assert.True(
                        result.Status is IdentityVerificationStatus.Unavailable or IdentityVerificationStatus.Rejected,
                        $"Protected process {name} (PID {proc.Id}) returned unexpected status {result.Status}");
                }
            }
            finally
            {
                foreach (var proc in processes)
                    proc.Dispose();
            }
        }

        Assert.True(inspectedAny, "Expected at least one Windows system process (csrss/audiodg/smss/services/lsass) to be present.");
    }

    [Theory]
    [InlineData(true, false, false, "process-access-denied")]
    [InlineData(false, true, false, "process-token-access-denied")]
    [InlineData(false, false, true, "process-token-access-denied")]
    public async Task AccessDenied_Win32DeniedAtOpenTokenOrQuery_ReturnsExpectedUnavailable(
        bool denyOpenProcess,
        bool denyOpenToken,
        bool denyTokenUserQuery,
        string expectedReason)
    {
        var probe = new ScriptedWindowsProcessProbe
        {
            OpenProcessResult = !denyOpenProcess,
            OpenProcessError = denyOpenProcess ? WindowsSecurityApis.ERROR_ACCESS_DENIED : WindowsSecurityApis.ERROR_SUCCESS,
            OpenTokenResult = !denyOpenToken,
            OpenTokenError = denyOpenToken ? WindowsSecurityApis.ERROR_ACCESS_DENIED : WindowsSecurityApis.ERROR_SUCCESS,
            GetTokenUserResult = !denyTokenUserQuery,
            GetTokenUserError = denyTokenUserQuery ? WindowsSecurityApis.ERROR_ACCESS_DENIED : WindowsSecurityApis.ERROR_SUCCESS
        };
        var verifier = new WindowsProcessIdentityVerifier(probe, () => true, () => BaseNowUtc);

        var result = await verifier.VerifyProcessAsync(ValidClaims(4), VerifiedSession(SubjectSidAlice));

        Assert.Equal(IdentityVerificationStatus.Unavailable, result.Status);
        Assert.False(result.IsTrusted);
        Assert.Null(result.Evidence);
        Assert.Equal(expectedReason, result.ReasonCode);
        Assert.True(probe.AllHandlesDisposed);
    }

    // -------------------------------------------------------------------------
    // 4. Cross-user process verification
    // -------------------------------------------------------------------------

    [Fact]
    public async Task CrossUser_ProcessOwnedByDifferentSid_RejectsOwnerSidMismatch()
    {
        var probe = new ScriptedWindowsProcessProbe
        {
            TokenUserSid = SubjectSidBob
        };
        var verifier = new WindowsProcessIdentityVerifier(probe, () => true, () => BaseNowUtc);

        var result = await verifier.VerifyProcessAsync(
            ValidClaims(7001, sid: SubjectSidAlice),
            VerifiedSession(SubjectSidAlice));

        Assert.Equal(IdentityVerificationStatus.Rejected, result.Status);
        Assert.False(result.IsTrusted);
        Assert.Null(result.Evidence);
        Assert.Equal("process-owner-sid-mismatch", result.ReasonCode);
        Assert.True(probe.AllHandlesDisposed);
    }

    [Fact]
    public async Task CrossUser_CallerSpoofsSessionSidAndTokenOwnedBySystem_RejectsOwnerSidMismatch()
    {
        var probe = new ScriptedWindowsProcessProbe
        {
            TokenUserSid = SystemSid
        };
        var verifier = new WindowsProcessIdentityVerifier(probe, () => true, () => BaseNowUtc);

        var result = await verifier.VerifyProcessAsync(
            ValidClaims(7002, sid: SubjectSidAlice),
            VerifiedSession(SubjectSidAlice));

        Assert.Equal(IdentityVerificationStatus.Rejected, result.Status);
        Assert.False(result.IsTrusted);
        Assert.Null(result.Evidence);
        Assert.Equal("process-owner-sid-mismatch", result.ReasonCode);
        Assert.True(probe.AllHandlesDisposed);
    }

    [Fact]
    public async Task CrossUser_LiveProcessVerifiedAgainstDifferentUserSession_RejectsOwnerSidMismatch()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Assert.True(WindowsSessionIdentityVerifier.TryReadCurrentProcessTokenIdentity(out var osIdentity, out var error), error);
        var otherSid = string.Equals(osIdentity.UserSid, SubjectSidBob, StringComparison.Ordinal)
            ? SubjectSidAlice
            : SubjectSidBob;

        using var current = Process.GetCurrentProcess();
        var startUtc = current.StartTime.ToUniversalTime();
        var verifier = new WindowsProcessIdentityVerifier();

        // Case A: Both session and claims specify the other user SID.
        var resultBothOther = await verifier.VerifyProcessAsync(
            new CallerProcessIdentityClaims(current.Id, startUtc, otherSid, SyntheticPath, SyntheticHash),
            VerifiedSession(otherSid));

        Assert.Equal(IdentityVerificationStatus.Rejected, resultBothOther.Status);
        Assert.False(resultBothOther.IsTrusted);
        Assert.Equal("process-owner-sid-mismatch", resultBothOther.ReasonCode);

        // Case B: Session belongs to other user, caller claims actual OS user SID.
        var resultSessionOther = await verifier.VerifyProcessAsync(
            new CallerProcessIdentityClaims(current.Id, startUtc, osIdentity.UserSid, SyntheticPath, SyntheticHash),
            VerifiedSession(otherSid));

        Assert.Equal(IdentityVerificationStatus.Rejected, resultSessionOther.Status);
        Assert.False(resultSessionOther.IsTrusted);
        Assert.Equal("process-owner-sid-mismatch", resultSessionOther.ReasonCode);
    }

    // -------------------------------------------------------------------------
    // 5. Handle revalidation guarantee (TOCTOU prevention)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task HandleRevalidation_ReusesSingleOpenedHandleAcrossAllChecksAndDoubleCheck()
    {
        var probe = new ScriptedWindowsProcessProbe();
        var verifier = new WindowsProcessIdentityVerifier(probe, () => true, () => BaseNowUtc);

        var result = await verifier.VerifyProcessAsync(ValidClaims(8001), VerifiedSession(SubjectSidAlice));

        Assert.Equal(IdentityVerificationStatus.Verified, result.Status);
        Assert.True(result.IsTrusted);
        Assert.Equal(1, probe.OpenProcessCalls);
        Assert.Equal(2, probe.GetProcessTimesCalls);
        Assert.Equal(2, probe.GetExitCodeCalls);
        Assert.Equal(2, probe.WaitCalls);
        Assert.Equal(1, probe.OpenTokenCalls);
        Assert.NotNull(probe.OpenedProcessHandle);
        Assert.Equal(7, probe.ObservedProcessHandles.Count);
        Assert.All(probe.ObservedProcessHandles, h => Assert.Same(probe.OpenedProcessHandle, h));
        Assert.True(probe.AllHandlesDisposed);
    }

    [Fact]
    public async Task HandleRevalidation_DetectsSingleTickCreationTimeShiftOnRecheck_Rejects()
    {
        // Initial creation time matches caller claim, but recheck on the same handle differs by 1 tick
        // (far below the 1ms initial tolerance). Recheck must enforce exact equality and reject.
        var probe = new ScriptedWindowsProcessProbe
        {
            CreationTimeSequence = [BaseStartUtc, BaseStartUtc.AddTicks(1)]
        };
        var verifier = new WindowsProcessIdentityVerifier(probe, () => true, () => BaseNowUtc);

        var result = await verifier.VerifyProcessAsync(ValidClaims(8002), VerifiedSession(SubjectSidAlice));

        Assert.Equal(IdentityVerificationStatus.Rejected, result.Status);
        Assert.False(result.IsTrusted);
        Assert.Null(result.Evidence);
        Assert.Equal("process-pid-reused-or-start-time-mismatch", result.ReasonCode);
        Assert.Equal(1, probe.OpenProcessCalls);
        Assert.Equal(2, probe.GetProcessTimesCalls);
        Assert.True(probe.AllHandlesDisposed);
    }

    [Fact]
    public async Task HandleRevalidation_RecheckProbeFailuresFailClosedAndDisposeHandles()
    {
        var timesRecheckFailProbe = new ScriptedWindowsProcessProbe
        {
            GetProcessTimesOverride = callIndex => callIndex == 0
                ? (BaseStartUtc, false, WindowsSecurityApis.ERROR_SUCCESS)
                : (default, false, WindowsSecurityApis.ERROR_ACCESS_DENIED)
        };
        var verifier1 = new WindowsProcessIdentityVerifier(timesRecheckFailProbe, () => true, () => BaseNowUtc);
        var res1 = await verifier1.VerifyProcessAsync(ValidClaims(8003), VerifiedSession(SubjectSidAlice));
        Assert.Equal(IdentityVerificationStatus.Unavailable, res1.Status);
        Assert.Equal("process-times-recheck-failed: 5", res1.ReasonCode);
        Assert.True(timesRecheckFailProbe.AllHandlesDisposed);

        var exitRecheckFailProbe = new ScriptedWindowsProcessProbe
        {
            GetExitCodeOverride = callIndex => callIndex == 0
                ? (WindowsSecurityApis.STILL_ACTIVE, WindowsSecurityApis.ERROR_SUCCESS)
                : (0u, 6)
        };
        var verifier2 = new WindowsProcessIdentityVerifier(exitRecheckFailProbe, () => true, () => BaseNowUtc);
        var res2 = await verifier2.VerifyProcessAsync(ValidClaims(8004), VerifiedSession(SubjectSidAlice));
        Assert.Equal(IdentityVerificationStatus.Unavailable, res2.Status);
        Assert.Equal("process-exit-code-recheck-failed: 6", res2.ReasonCode);
        Assert.True(exitRecheckFailProbe.AllHandlesDisposed);
    }

    [Fact]
    public async Task HandleRevalidation_ConcurrentVerificationsAcrossThreads_AreIsolatedAndDeterministic()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Assert.True(WindowsSessionIdentityVerifier.TryReadCurrentProcessTokenIdentity(out var osIdentity, out var error), error);
        using var current = Process.GetCurrentProcess();
        var startUtc = current.StartTime.ToUniversalTime();
        var verifier = new WindowsProcessIdentityVerifier();
        var session = VerifiedSession(osIdentity.UserSid);
        var validClaims = new CallerProcessIdentityClaims(current.Id, startUtc, osIdentity.UserSid, SyntheticPath, SyntheticHash);
        var mismatchedClaims = new CallerProcessIdentityClaims(current.Id, startUtc.AddSeconds(-10), osIdentity.UserSid, SyntheticPath, SyntheticHash);

        var tasks = Enumerable.Range(0, 16).Select(i => Task.Run(async () =>
        {
            var isValid = i % 2 == 0;
            var res = await verifier.VerifyProcessAsync(isValid ? validClaims : mismatchedClaims, session);
            return (isValid, res);
        })).ToArray();

        var outcomes = await Task.WhenAll(tasks);

        foreach (var (isValid, res) in outcomes)
        {
            if (isValid)
            {
                Assert.True(res.IsTrusted);
                Assert.Equal(IdentityVerificationStatus.Verified, res.Status);
                Assert.Equal(current.Id, res.Evidence!.ProcessId);
            }
            else
            {
                Assert.False(res.IsTrusted);
                Assert.Equal(IdentityVerificationStatus.Rejected, res.Status);
                Assert.Equal("process-pid-reused-or-start-time-mismatch", res.ReasonCode);
            }
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static Process StartShortLivedChildProcess(string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = arguments,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        return Process.Start(startInfo)!;
    }

    private static CallerProcessIdentityClaims ValidClaims(
        int processId,
        DateTimeOffset? startUtc = null,
        string sid = SubjectSidAlice) =>
        new(processId, startUtc ?? BaseStartUtc, sid, SyntheticPath, SyntheticHash);

    private static VerifiedSessionIdentity VerifiedSession(string sid) =>
        new(
            Guid.NewGuid(),
            1,
            sid,
            "0x1",
            BaseStartUtc.AddMinutes(-5),
            BaseStartUtc.AddMinutes(-5),
            BaseStartUtc.AddHours(8));

    private sealed class ScriptedWindowsProcessProbe : WindowsProcessIdentityVerifier.IWindowsProcessProbe
    {
        public int OpenProcessCalls { get; private set; }
        public int GetProcessTimesCalls { get; private set; }
        public int GetExitCodeCalls { get; private set; }
        public int WaitCalls { get; private set; }
        public int OpenTokenCalls { get; private set; }

        public WindowsProcessIdentityVerifier.IProcessHandle? OpenedProcessHandle { get; private set; }
        public List<WindowsProcessIdentityVerifier.IProcessHandle> ObservedProcessHandles { get; } = [];
        public bool ProcessHandleDisposed { get; private set; }
        public bool TokenHandleDisposed { get; private set; }
        public bool AllHandlesDisposed =>
            (OpenedProcessHandle is null || ProcessHandleDisposed)
            && (OpenTokenCalls == 0 || !OpenTokenResult || TokenHandleDisposed);

        public bool OpenProcessResult { get; init; } = true;
        public int OpenProcessError { get; init; }
        public DateTimeOffset[] CreationTimeSequence { get; init; } = [BaseStartUtc, BaseStartUtc];
        public bool[] HasExitTimeSequence { get; init; } = [false, false];
        public uint[] ExitCodeSequence { get; init; } = [WindowsSecurityApis.STILL_ACTIVE, WindowsSecurityApis.STILL_ACTIVE];
        public uint[] WaitSequence { get; init; } = [WindowsSecurityApis.WAIT_TIMEOUT, WindowsSecurityApis.WAIT_TIMEOUT];
        public int WaitError { get; init; }
        public bool OpenTokenResult { get; init; } = true;
        public int OpenTokenError { get; init; }
        public bool GetTokenUserResult { get; init; } = true;
        public int GetTokenUserError { get; init; }
        public string TokenUserSid { get; init; } = SubjectSidAlice;
        public uint TokenSessionId { get; init; } = 1;
        public uint PidSessionId { get; init; } = 1;

        public Action? OnOpenProcessToken { get; init; }
        public Action? OnProcessIdToSessionId { get; init; }
        public Func<int, (DateTimeOffset creationTimeUtc, bool hasExitTime, int error)>? GetProcessTimesOverride { get; init; }
        public Func<int, (uint exitCode, int error)>? GetExitCodeOverride { get; init; }

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

            OpenedProcessHandle = new TrackedProcessHandle(() => ProcessHandleDisposed = true);
            handle = OpenedProcessHandle;
            win32Error = WindowsSecurityApis.ERROR_SUCCESS;
            return true;
        }

        public bool TryGetProcessTimes(
            WindowsProcessIdentityVerifier.IProcessHandle handle,
            out DateTimeOffset creationTimeUtc,
            out bool hasExitTime,
            out int win32Error)
        {
            ObservedProcessHandles.Add(handle);
            var callIndex = GetProcessTimesCalls++;
            if (GetProcessTimesOverride is not null)
            {
                var overrideResult = GetProcessTimesOverride(callIndex);
                creationTimeUtc = overrideResult.creationTimeUtc;
                hasExitTime = overrideResult.hasExitTime;
                win32Error = overrideResult.error;
                return win32Error == WindowsSecurityApis.ERROR_SUCCESS;
            }

            creationTimeUtc = CreationTimeSequence[Math.Min(callIndex, CreationTimeSequence.Length - 1)];
            hasExitTime = HasExitTimeSequence[Math.Min(callIndex, HasExitTimeSequence.Length - 1)];
            win32Error = WindowsSecurityApis.ERROR_SUCCESS;
            return true;
        }

        public bool TryGetExitCode(
            WindowsProcessIdentityVerifier.IProcessHandle handle,
            out uint exitCode,
            out int win32Error)
        {
            ObservedProcessHandles.Add(handle);
            var callIndex = GetExitCodeCalls++;
            if (GetExitCodeOverride is not null)
            {
                var overrideResult = GetExitCodeOverride(callIndex);
                exitCode = overrideResult.exitCode;
                win32Error = overrideResult.error;
                return win32Error == WindowsSecurityApis.ERROR_SUCCESS;
            }

            exitCode = ExitCodeSequence[Math.Min(callIndex, ExitCodeSequence.Length - 1)];
            win32Error = WindowsSecurityApis.ERROR_SUCCESS;
            return true;
        }

        public uint WaitForExitZero(WindowsProcessIdentityVerifier.IProcessHandle handle, out int win32Error)
        {
            ObservedProcessHandles.Add(handle);
            var callIndex = WaitCalls++;
            var result = WaitSequence[Math.Min(callIndex, WaitSequence.Length - 1)];
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
            ObservedProcessHandles.Add(processHandle);
            OpenTokenCalls++;
            OnOpenProcessToken?.Invoke();

            if (!OpenTokenResult)
            {
                tokenHandle = null;
                win32Error = OpenTokenError;
                return false;
            }

            tokenHandle = new TrackedTokenHandle(() => TokenHandleDisposed = true);
            win32Error = WindowsSecurityApis.ERROR_SUCCESS;
            return true;
        }

        public bool TryGetTokenUserSid(
            WindowsProcessIdentityVerifier.IProcessTokenHandle tokenHandle,
            out string? sid,
            out int win32Error)
        {
            if (!GetTokenUserResult)
            {
                sid = null;
                win32Error = GetTokenUserError;
                return false;
            }

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
            OnProcessIdToSessionId?.Invoke();
            sessionId = PidSessionId;
            win32Error = WindowsSecurityApis.ERROR_SUCCESS;
            return true;
        }

        private sealed class TrackedProcessHandle(Action onDispose) : WindowsProcessIdentityVerifier.IProcessHandle
        {
            public void Dispose() => onDispose();
        }

        private sealed class TrackedTokenHandle(Action onDispose) : WindowsProcessIdentityVerifier.IProcessTokenHandle
        {
            public void Dispose() => onDispose();
        }
    }
}
