using Boundless.Protection.Engine;

if (OperatingSystem.IsWindows())
{
    NativeMethods.SetErrorMode(0x0001 | 0x0002 | 0x8000); // SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX
}

if (args.Length is < 3 or > 4 || args[0] is not ("crash-before-apply" or "crash-after-partial-apply" or "crash-after-complete-apply" or "crash-during-restore" or "crash-after-restore" or "apply" or "hold-lock"))
{
    Console.Error.WriteLine("Expected a BPE synthetic crash/apply/lock mode plus journal and provider database paths.");
    return 64;
}

var journal = new SqliteProtectionJournal(args[1]);
var provider = new PersistentSimulatedPolicyProvider(args[2], ProtectionProviderKind.AppLocker);
if (args[0] == "hold-lock")
{
    using var lease = journal.AcquireOperationLock();
    File.WriteAllText(args[3], "locked");
    await Task.Delay(TimeSpan.FromSeconds(30));
    return 0;
}
if (args[0] == "apply")
{
    var applied = await new ProtectionEngine([provider], journal, new InMemoryProtectionAuditSink()).ApplyAsync(CreateRequest());
    Console.WriteLine(applied.Outcome);
    return applied.Outcome is ProtectionOutcome.Applied or ProtectionOutcome.Duplicate ? 0 : 1;
}
if (args[0] == "crash-after-restore")
{
    var restoringEngine = new ProtectionEngine([new CrashAfterRestoreProvider(provider)], journal, new InMemoryProtectionAuditSink());
    await restoringEngine.RestoreAsync(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), ProtectionProviderKind.AppLocker);
    return 0;
}
if (args[0] == "crash-during-restore")
{
    provider.CrashRestoreAfter(1);
    try
    {
        await new ProtectionEngine([provider], journal, new InMemoryProtectionAuditSink()).RestoreAsync(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), ProtectionProviderKind.AppLocker);
    }
    catch (SimulatedApplicationCrashException)
    {
        Environment.FailFast("Intentional BPE synthetic crash during restore mutation.");
    }
    return 71;
}

if (args[0] == "crash-before-apply")
{
    try
    {
        await new ProtectionEngine([new CrashBeforeApplyProvider(provider)], journal, new InMemoryProtectionAuditSink()).ApplyAsync(CreateRequest());
    }
    catch (SimulatedApplicationCrashException)
    {
        Environment.FailFast("Intentional BPE synthetic crash before provider mutation.");
    }
    return 72;
}

provider.CrashApplyAfter(args[0] == "crash-after-complete-apply" ? 2 : 1);
var request = CreateRequest();
var engine = new ProtectionEngine([provider], journal, new InMemoryProtectionAuditSink());
try
{
    await engine.ApplyAsync(request);
    return 0;
}
catch (SimulatedApplicationCrashException)
{
    // Simulate an abrupt OS process failure after SQLite committed the mutation.
    Environment.FailFast("Intentional BPE synthetic crash injection after provider commit.");
    return 70;
}

static ProtectionRequest CreateRequest() => new(
    Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
    Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
    1,
    "S-1-5-21-111111111-222222222-333333333-1001",
    ProtectionProviderKind.AppLocker,
    [
        new RequestedRule(Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
            "S-1-5-21-111111111-222222222-333333333-1001",
            new ApplicationIdentity("C:\\BPE-Synthetic\\child-one.exe", new string('a', 64))),
        new RequestedRule(Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
            "S-1-5-21-111111111-222222222-333333333-1001",
            new ApplicationIdentity("C:\\BPE-Synthetic\\child-two.exe", new string('b', 64)))
    ]);

sealed class CrashAfterRestoreProvider(IProtectionProvider inner) : IProtectionProvider
{
    public ProtectionProviderKind Kind => inner.Kind;
    public Task<PolicySnapshot> SnapshotAsync(CancellationToken token = default) => inner.SnapshotAsync(token);
    public Task<ProviderMutationResult> ApplyAsync(Guid ownerId, IReadOnlyList<RequestedRule> rules, long version, string fingerprint, CancellationToken token = default) => inner.ApplyAsync(ownerId, rules, version, fingerprint, token);
    public Task<IReadOnlyList<ProtectionRule>> GetOwnedRulesAsync(Guid ownerId, CancellationToken token = default) => inner.GetOwnedRulesAsync(ownerId, token);
    public async Task<ProviderMutationResult> RestoreAsync(Guid ownerId, IReadOnlyList<ProtectionRule> rules, CancellationToken token = default)
    {
        var result = await inner.RestoreAsync(ownerId, rules, token);
        Environment.FailFast("Intentional BPE synthetic crash after provider restore commit.");
        return result;
    }
}

sealed class CrashBeforeApplyProvider(IProtectionProvider inner) : IProtectionProvider
{
    public ProtectionProviderKind Kind => inner.Kind;
    public Task<PolicySnapshot> SnapshotAsync(CancellationToken token = default) => inner.SnapshotAsync(token);
    public Task<ProviderMutationResult> ApplyAsync(Guid ownerId, IReadOnlyList<RequestedRule> rules, long version, string fingerprint, CancellationToken token = default) => throw new SimulatedApplicationCrashException();
    public Task<ProviderMutationResult> RestoreAsync(Guid ownerId, IReadOnlyList<ProtectionRule> rules, CancellationToken token = default) => inner.RestoreAsync(ownerId, rules, token);
    public Task<IReadOnlyList<ProtectionRule>> GetOwnedRulesAsync(Guid ownerId, CancellationToken token = default) => inner.GetOwnedRulesAsync(ownerId, token);
}

static partial class NativeMethods
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint SetErrorMode(uint uMode);
}
