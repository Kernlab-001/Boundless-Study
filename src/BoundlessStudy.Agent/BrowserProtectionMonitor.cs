using System.Diagnostics;
using BoundlessStudy.Application.Abstractions;
using BoundlessStudy.Application.EventRecording;
using BoundlessStudy.Application.UseCases;
using BoundlessStudy.Domain;
using BoundlessStudy.Domain.Entities;
using BoundlessStudy.Infrastructure.Persistence;

namespace BoundlessStudy.Agent;

/// <summary>
/// Agent-owned monitor that keeps persisted browser protection state and gap time
/// aligned with extension acknowledgements. Elapsed gaps use a monotonic clock.
/// </summary>
public sealed class BrowserProtectionMonitor
{
    private readonly SqliteDataStore _store;
    private readonly ILocalClock _clock;
    private readonly Func<Guid?, bool> _isPolicyApplied;
    private readonly TimeSpan _pollInterval;

    public BrowserProtectionMonitor(
        SqliteDataStore store,
        ILocalClock clock,
        Func<Guid?, bool> isPolicyApplied,
        TimeSpan? pollInterval = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _isPolicyApplied = isPolicyApplied ?? throw new ArgumentNullException(nameof(isPolicyApplied));
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(5);
        if (_pollInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(pollInterval));
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_pollInterval);
        Guid? gapSessionId = null;
        double fractionalGapSeconds = 0;
        var lastTick = Stopwatch.GetTimestamp();

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                try
                {
                    var tick = Stopwatch.GetTimestamp();
                    var elapsedSeconds = Math.Max(0, (tick - lastTick) / (double)Stopwatch.Frequency);
                    lastTick = tick;

                await using var repos = _store.CreateRepositories();
                var profile = await repos.LocalProfiles.FindSingleAsync(cancellationToken);
                if (profile is null) continue;
                var session = await repos.FocusSessions.FindActiveByProfileAsync(profile.Id, cancellationToken);
                if (session is null || !session.IsActive)
                {
                    gapSessionId = null;
                    fractionalGapSeconds = 0;
                    continue;
                }

                var sitesConfigured = await repos.BlockedSites.CountEnabledAsync(profile.Id, cancellationToken) > 0;
                if (!sitesConfigured || string.Equals(session.ProfileCode, BuiltInProfiles.FlexibleCode, StringComparison.OrdinalIgnoreCase))
                {
                    gapSessionId = null;
                    fractionalGapSeconds = 0;
                    if (session.ProtectionStatus != ProtectionStatus.NotRequired)
                    {
                        var noProtection = new SystemStateUseCases(repos.FocusSessions, repos.UnitOfWork, _clock, new SessionEventRecorder(repos.SessionEvents, _clock));
                        await noProtection.HandleProtectionStatusChangedAsync(session.Id, ProtectionStatus.NotRequired, VerificationStatus.Unverified, cancellationToken);
                    }
                    continue;
                }

                var verified = _isPolicyApplied(session.Id);
                var status = verified ? ProtectionStatus.Protected : ProtectionStatus.Degraded;
                var verification = verified ? VerificationStatus.Verified : VerificationStatus.Degraded;
                var state = new SystemStateUseCases(repos.FocusSessions, repos.UnitOfWork, _clock, new SessionEventRecorder(repos.SessionEvents, _clock));
                await state.HandleProtectionStatusChangedAsync(session.Id, status, verification, cancellationToken);

                if (verified)
                {
                    gapSessionId = null;
                    fractionalGapSeconds = 0;
                    continue;
                }

                if (gapSessionId != session.Id)
                {
                    gapSessionId = session.Id;
                    fractionalGapSeconds = 0;
                }

                    fractionalGapSeconds += elapsedSeconds;
                    var wholeSeconds = (int)Math.Floor(fractionalGapSeconds);
                    if (wholeSeconds > 0)
                    {
                        fractionalGapSeconds -= wholeSeconds;
                        await state.RecordProtectionGapAsync(session.Id, wholeSeconds, cancellationToken);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"BoundlessStudy.Agent: browser protection monitor tick failed ({ex.GetType().Name}).");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal Agent shutdown.
        }
    }
}
