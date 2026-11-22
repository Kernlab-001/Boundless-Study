using BoundlessStudy.Application.Abstractions;
using BoundlessStudy.Infrastructure.Persistence.Repositories;

namespace BoundlessStudy.Infrastructure.Persistence;

/// <summary>
/// One agent command/query scope. All sixteen repository ports share a single
/// <see cref="BoundlessDbContext"/> and <see cref="SqliteUnitOfWork"/> so a domain
/// operation and its reward side effects commit atomically (§8.11).
/// Update-only handlers must still be wrapped by the agent in
/// <see cref="IUnitOfWork.BeginAsync"/>/<see cref="IUnitOfWork.CommitAsync"/>.
/// </summary>
public sealed class SqliteRepositorySet : IAsyncDisposable
{
    private readonly BoundlessDbContext _context;
    private readonly SqliteUnitOfWork _unitOfWork;

    public SqliteRepositorySet(BoundlessDbContext context)
        : this(context, PersistenceConstants.BusyTimeoutMilliseconds)
    {
    }

    internal SqliteRepositorySet(BoundlessDbContext context, int writeGateTimeoutMilliseconds)
    {
        _context = context;
        _unitOfWork = new SqliteUnitOfWork(context, writeGateTimeoutMilliseconds);
        LocalProfiles = new LocalProfileRepository(context);
        Subjects = new SubjectRepository(context);
        StudyTasks = new StudyTaskRepository(context);
        FocusProfiles = new FocusProfileRepository(context);
        FocusSessions = new FocusSessionRepository(context);
        BreakSessions = new BreakSessionRepository(context);
        SessionEvents = new SessionEventRepository(context);
        SessionExceptions = new SessionExceptionRepository(context);
        BlockedApps = new BlockedAppRepository(context);
        BlockedSites = new BlockedSiteRepository(context);
        AllowedSites = new AllowedSiteRepository(context);
        RewardLedger = new RewardLedgerRepository(context);
        Achievements = new AchievementRepository(context);
        UserAchievements = new UserAchievementRepository(context);
        AppSettings = new AppSettingRepository(context);
        BackupRecords = new BackupRecordRepository(context);
        UnitOfWork = _unitOfWork;
        RewardState = new SqliteRewardStateProvider(context);
    }

    public ILocalProfileRepository LocalProfiles { get; }

    public ISubjectRepository Subjects { get; }

    public IStudyTaskRepository StudyTasks { get; }

    public IFocusProfileRepository FocusProfiles { get; }

    public IFocusSessionRepository FocusSessions { get; }

    public IBreakSessionRepository BreakSessions { get; }

    public ISessionEventRepository SessionEvents { get; }

    public ISessionExceptionRepository SessionExceptions { get; }

    public IBlockedAppRepository BlockedApps { get; }

    public IBlockedSiteRepository BlockedSites { get; }

    public IAllowedSiteRepository AllowedSites { get; }

    public IRewardLedgerRepository RewardLedger { get; }

    public IAchievementRepository Achievements { get; }

    public IUserAchievementRepository UserAchievements { get; }

    public IAppSettingRepository AppSettings { get; }

    public IBackupRecordRepository BackupRecords { get; }

    public IUnitOfWork UnitOfWork { get; }

    public IRewardStateProvider RewardState { get; }

    public BoundlessDbContext Context => _context;

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _unitOfWork.DisposeAsync();
        }
        finally
        {
            await _context.DisposeAsync();
        }
    }
}

/// <summary>
/// Owner-facing database entry point. Initialization must complete before any
/// context is created; this makes the R-P3-14 handshake a precondition of normal
/// repository access rather than an optional check.
/// </summary>
public sealed class SqliteDataStore : IAsyncDisposable
{
    private readonly SqliteDatabaseOptions _options;
    private readonly SqliteDatabaseBootstrapper _bootstrapper;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private SqliteBootstrapResult? _bootstrap;
    private bool _disposed;

    public SqliteDataStore(SqliteDatabaseOptions options, ILocalClock clock)
    {
        _options = options;
        _bootstrapper = new SqliteDatabaseBootstrapper(options, clock);
        Clock = clock;
    }

    public ILocalClock Clock { get; }

    public string DatabasePath => _options.DatabasePath;

    public string BackupDirectory => _options.ResolvedBackupDirectory;

    public bool IsInitialized => _bootstrap is not null;

    public SqliteBootstrapResult? Bootstrap => _bootstrap;

    public async Task<SqliteBootstrapResult> InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_bootstrap is not null)
        {
            return _bootstrap;
        }

        await _initializationGate.WaitAsync(cancellationToken);
        try
        {
            if (_bootstrap is null)
            {
                _bootstrap = await _bootstrapper.InitializeAsync(cancellationToken);
            }

            return _bootstrap;
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    /// <summary>Creates a context only after a successful handshake and seed.</summary>
    public BoundlessDbContext CreateContext()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_bootstrap is null)
        {
            throw new InvalidOperationException(
                "The database has not been initialized. Call InitializeAsync before creating a context.");
        }

        return new BoundlessDbContext(_options.BuildDbContextOptions());
    }

    public SqliteRepositorySet CreateRepositories() =>
        new(CreateContext(), _options.BusyTimeoutMilliseconds);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _initializationGate.Dispose();
        _disposed = true;
        await Task.CompletedTask;
    }
}
