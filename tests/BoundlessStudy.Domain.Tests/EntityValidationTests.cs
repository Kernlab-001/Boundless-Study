using BoundlessStudy.Domain;
using BoundlessStudy.Domain.Entities;

namespace BoundlessStudy.Domain.Tests;

/// <summary>Entity construction and validation rules from the conceptual data model §4.</summary>
public class EntityValidationTests
{
    private static readonly DateTime Now = new(2026, 11, 13, 10, 0, 0, DateTimeKind.Utc);

    // ---- StudyTask (§4.3) ----

    [Fact]
    public void Task_title_is_required_trimmed_and_limited_to_200()
    {
        Assert.Throws<DomainException>(() => StudyTask.Create(Guid.NewGuid(), "   ", Now));
        Assert.Throws<DomainException>(() => StudyTask.Create(Guid.NewGuid(), new string('a', 201), Now));

        var task = StudyTask.Create(Guid.NewGuid(), "  Practice algebra  ", Now);
        Assert.Equal("Practice algebra", task.Title);
        Assert.Equal(StudyTaskStatus.Todo, task.Status);
        Assert.Equal(TaskPriority.Normal, task.Priority);
    }

    [Fact]
    public void Task_estimate_must_be_5_to_600_in_5_minute_steps()
    {
        Assert.Throws<DomainException>(() => StudyTask.Create(Guid.NewGuid(), "t", Now, estimatedMinutes: 4));
        Assert.Throws<DomainException>(() => StudyTask.Create(Guid.NewGuid(), "t", Now, estimatedMinutes: 601));
        Assert.Throws<DomainException>(() => StudyTask.Create(Guid.NewGuid(), "t", Now, estimatedMinutes: 37));

        var task = StudyTask.Create(Guid.NewGuid(), "t", Now, estimatedMinutes: 600);
        Assert.Equal(600, task.EstimatedMinutes);
    }

    [Fact]
    public void Task_completion_awards_at_most_once()
    {
        var task = StudyTask.Create(Guid.NewGuid(), "t", Now);
        var first = task.Complete(Now);
        var second = task.Complete(Now.AddMinutes(1));

        Assert.True(first);
        Assert.False(second); // idempotent re-completion: no second award
        Assert.Equal(Now, task.CompletedAtUtc);
    }

    [Fact]
    public void Reopening_keeps_the_completion_and_its_reward()
    {
        var task = StudyTask.Create(Guid.NewGuid(), "t", Now);
        task.Complete(Now);
        task.AttachCompletionReward(Guid.NewGuid(), Now);

        task.Reopen(Now);
        Assert.Equal(StudyTaskStatus.Todo, task.Status);
        Assert.NotNull(task.CompletedAtUtc); // retained: re-completion cannot award again (TV-18)
        Assert.NotNull(task.CompletionRewardId);

        Assert.False(task.Complete(Now.AddMinutes(2))); // retained completion: no second award
        Assert.Equal(StudyTaskStatus.Done, task.Status); // but the task does return to Done
        Assert.Equal(Now, task.CompletedAtUtc);
    }

    [Fact]
    public void Deleted_task_cannot_complete()
    {
        var task = StudyTask.Create(Guid.NewGuid(), "t", Now);
        task.SoftDelete(Now);
        Assert.Throws<DomainException>(() => task.Complete(Now));
    }

    [Fact]
    public void Task_transitions_are_validated()
    {
        var task = StudyTask.Create(Guid.NewGuid(), "t", Now);
        Assert.Throws<DomainException>(() => task.Reopen(Now));     // not Done
        Assert.Throws<DomainException>(() => task.BackToTodo(Now)); // not InProgress
        Assert.Throws<DomainException>(() => task.Restore(Now));    // not Archived

        task.Start(Now);
        Assert.Equal(StudyTaskStatus.InProgress, task.Status);
        task.BackToTodo(Now);
        task.Archive(Now);
        task.Restore(Now);
    }

    [Fact]
    public void Completion_reward_can_be_attached_only_once()
    {
        var task = StudyTask.Create(Guid.NewGuid(), "t", Now);
        task.Complete(Now);
        var ledgerId = Guid.NewGuid();
        task.AttachCompletionReward(ledgerId, Now);

        Assert.Throws<DomainException>(() => task.AttachCompletionReward(Guid.NewGuid(), Now));
        task.AttachCompletionReward(ledgerId, Now); // same entry is idempotent
    }

    // ---- Subject (§4.2) ----

    [Fact]
    public void Subject_name_is_required_and_limited_to_100()
    {
        Assert.Throws<DomainException>(() => Subject.Create(Guid.NewGuid(), "", null, Now));
        Assert.Throws<DomainException>(() => Subject.Create(Guid.NewGuid(), new string('a', 101), null, Now));

        var subject = Subject.Create(Guid.NewGuid(), "Math", "   ", Now);
        Assert.Equal("Math", subject.Name);
        Assert.Equal(string.Empty, subject.Description);
    }

    [Fact]
    public void Subject_archive_restores_and_rename_never_touches_history()
    {
        var subject = Subject.Create(Guid.NewGuid(), "History", null, Now);
        subject.Archive(Now);
        Assert.True(subject.IsArchived);
        subject.Restore(Now);
        Assert.False(subject.IsArchived);
        subject.Rename("Ancient history", Now);
        Assert.Equal("Ancient history", subject.Name);
    }

    // ---- LocalProfile (§4.1) ----

    [Fact]
    public void Profile_defaults_and_cached_totals()
    {
        var profile = LocalProfile.Create(Now);
        Assert.Equal("My study profile", profile.DisplayName);
        Assert.Equal(InstallationMode.Limited, profile.InstallationMode);
        Assert.Equal(1, profile.CachedLevel);
        Assert.Equal("ECONOMY-1", profile.EconomyRuleVersion);

        Assert.Throws<DomainException>(() => profile.SetCachedRewardTotals(-1, 1, Now));
        Assert.Throws<DomainException>(() => profile.SetCachedRewardTotals(0, 0, Now));
        profile.SetCachedRewardTotals(1_500, 4, Now);
        Assert.Equal(1_500, profile.CachedTotalXp);
        Assert.Equal(4, profile.CachedLevel);
    }

    // ---- FocusProfile (§4.4) ----

    [Fact]
    public void Built_in_profiles_match_ECONOMY_1_exactly()
    {
        var flexible = BuiltInProfiles.Flexible();
        Assert.Equal(EarlyExitMode.Immediate, flexible.EarlyExitMode);
        Assert.Equal(0, flexible.EarlyExitCooldownSeconds);
        Assert.Equal(0, flexible.MaxExceptionsPerSession);
        Assert.Equal(2, flexible.XpPerActiveMinute);
        Assert.False(flexible.RequiresBrowserWhenSiteRulesExist);

        var balanced = BuiltInProfiles.Balanced();
        Assert.Equal(EarlyExitMode.ConfirmationCooldown, balanced.EarlyExitMode);
        Assert.Equal(10, balanced.EarlyExitCooldownSeconds);
        Assert.Equal(2, balanced.MaxExceptionsPerSession);
        Assert.Equal(300, balanced.ExceptionDurationSeconds);
        Assert.Equal(0, balanced.ExceptionDelaySeconds);
        Assert.Equal(3, balanced.XpPerActiveMinute);
        Assert.False(balanced.RequiresBrowserWhenSiteRulesExist);

        var strict = BuiltInProfiles.Strict();
        Assert.Equal(EarlyExitMode.TypedConfirmationCooldown, strict.EarlyExitMode);
        Assert.Equal(30, strict.EarlyExitCooldownSeconds);
        Assert.Equal(1, strict.MaxExceptionsPerSession);
        Assert.Equal(120, strict.ExceptionDurationSeconds);
        Assert.Equal(30, strict.ExceptionDelaySeconds);
        Assert.Equal(4, strict.XpPerActiveMinute);
        Assert.True(strict.RequiresBrowserWhenSiteRulesExist);

        foreach (var profile in BuiltInProfiles.Seed())
        {
            Assert.True(profile.IsEnabled);
            Assert.Equal("ECONOMY-1", profile.EconomyRuleVersion);
        }
    }

    // ---- BlockedApp / BlockedSite / AllowedSite (§4.9–4.11) ----

    [Fact]
    public void Blocked_app_rejects_product_and_system_critical_targets()
    {
        var profileId = Guid.NewGuid();

        foreach (var name in new[] { "BoundlessStudy.Agent.exe", "csrss.exe", "svchost.exe", "osk.exe", "explorer.exe" })
        {
            Assert.Throws<DomainException>(() => BlockedApp.Create(profileId, name, null, name, Now));
        }

        var app = BlockedApp.Create(profileId, "Game", @"C:\Games\Game.EXE", null, Now);
        Assert.Equal("game.exe", app.NormalizedExecutableName); // required fallback derived from path
        Assert.Equal(@"C:\Games\Game.EXE", app.NormalizedExecutablePath);
        Assert.True(app.IsEnabled);
    }

    [Fact]
    public void Blocked_app_requires_a_target()
    {
        Assert.Throws<DomainException>(() => BlockedApp.Create(Guid.NewGuid(), "nothing", null, null, Now));
        Assert.Throws<DomainException>(() => BlockedApp.Create(Guid.NewGuid(), "nothing", null, "   ", Now));
    }

    [Fact]
    public void Blocked_and_allowed_sites_normalize_their_domain()
    {
        var site = BlockedSite.Create(Guid.NewGuid(), "  WWW.Example.COM ", null, Now);
        Assert.Equal("example.com", site.NormalizedDomain);
        Assert.Null(site.DisplayName);

        var allowed = AllowedSite.Create(Guid.NewGuid(), "docs.example.org", "Docs", Now);
        Assert.Equal("docs.example.org", allowed.NormalizedDomain);
        Assert.Equal("Docs", allowed.DisplayName);

        Assert.Throws<DomainException>(() => BlockedSite.Create(Guid.NewGuid(), "https://example.com/page", null, Now));
    }

    // ---- RewardLedgerEntry (§4.12) ----

    [Fact]
    public void Ledger_amount_must_be_positive()
    {
        Assert.Throws<DomainException>(() => RewardLedgerEntry.Create(
            Guid.NewGuid(), CurrencyCode.Xp, 0, LedgerSourceType.TaskCompletion, "t1", Now, DateOnly.FromDateTime(Now), 0, "ECONOMY-1"));
        Assert.Throws<DomainException>(() => RewardLedgerEntry.Create(
            Guid.NewGuid(), CurrencyCode.Xp, -5, LedgerSourceType.TaskCompletion, "t1", Now, DateOnly.FromDateTime(Now), 0, "ECONOMY-1"));
    }

    [Fact]
    public void Ledger_entries_form_exact_mvp_idempotency_keys()
    {
        var entry = RewardLedgerEntry.Create(
            Guid.NewGuid(), CurrencyCode.Xp, 25, LedgerSourceType.TaskCompletion, Guid.NewGuid().ToString(),
            Now, DateOnly.FromDateTime(Now), 0, "ECONOMY-1");
        Assert.StartsWith("TaskCompletion:", entry.IdempotencyKey);
        Assert.EndsWith(":XP", entry.IdempotencyKey);
        Assert.Equal("ECONOMY-1", entry.EconomyRuleVersion);
    }

    // ---- AchievementDefinition (§4.13) ----

    [Fact]
    public void Achievement_criteria_and_threshold_are_validated()
    {
        Assert.Throws<DomainException>(() => AchievementDefinition.Create("ACH-099", "n", "d", AchievementCriteriaType.StreakDays, 0, 50, "ECONOMY-1"));
        Assert.Throws<DomainException>(() => AchievementDefinition.Create("ACH-099", "n", "d", AchievementCriteriaType.StreakDays, 3, 0, "ECONOMY-1"));
        Assert.Throws<DomainException>(() => AchievementDefinition.Create("", "n", "d", AchievementCriteriaType.StreakDays, 3, 50, "ECONOMY-1"));
    }

    // ---- AppSetting (§4.15) ----

    [Fact]
    public void App_setting_values_must_be_valid_json()
    {
        var setting = AppSetting.Create("default.profile", "\"Balanced\"", Now);
        Assert.Equal("\"Balanced\"", setting.ValueJson);

        Assert.Throws<DomainException>(() => AppSetting.Create("k", "{not json", Now));
        Assert.Throws<DomainException>(() => AppSetting.Create("", "{}", Now));
        Assert.Throws<DomainException>(() => setting.Update("{broken", Now));
    }

    // ---- BackupRecord (§4.16) ----

    [Fact]
    public void Backup_record_validates_checksum_size_and_versions()
    {
        var sha = new string('a', 64);
        var record = BackupRecord.Create(Guid.NewGuid(), "backup-1.zip", BackupReason.PreMigration, "1", "0.1.0", sha, 1024, Now);
        Assert.Equal(sha, record.Sha256);

        Assert.Throws<DomainException>(() => BackupRecord.Create(Guid.NewGuid(), "b.zip", BackupReason.Manual, "1", "0.1.0", new string('z', 63), 1, Now));
        Assert.Throws<DomainException>(() => BackupRecord.Create(Guid.NewGuid(), "b.zip", BackupReason.Manual, "1", "0.1.0", sha, -1, Now));
        Assert.Throws<DomainException>(() => BackupRecord.Create(Guid.NewGuid(), "", BackupReason.Manual, "1", "0.1.0", sha, 1, Now));

        record.MarkVerified(Now);
        Assert.NotNull(record.VerifiedAtUtc);
    }

    // ---- SessionEvent (§4.7) ----

    [Fact]
    public void Session_events_require_positive_sequence_and_limited_payload()
    {
        var id = Guid.NewGuid();
        Assert.Throws<DomainException>(() => SessionEvent.Create(SessionType.Focus, id, 0, SessionEventType.Start, Now, DateOnly.FromDateTime(Now), 0, "", Now));
        Assert.Throws<DomainException>(() => SessionEvent.Create(
            SessionType.Focus, id, 1, SessionEventType.Start, Now, DateOnly.FromDateTime(Now), 0, new string('x', 2001), Now));

        var e = SessionEvent.Create(SessionType.Focus, id, 1, SessionEventType.Start, Now, DateOnly.FromDateTime(Now), 330, "", Now);
        Assert.Equal(1, e.Sequence);
        Assert.Equal(DateOnly.FromDateTime(Now), e.LocalDateAtEvent);
        Assert.Equal(330, e.UtcOffsetMinutes);
    }

    [Fact]
    public void Session_event_payload_accepts_exactly_2000_characters_and_rejects_2001()
    {
        var id = Guid.NewGuid();
        var atLimit = new string('p', SessionEvent.MaxPayloadJsonLength);

        var accepted = SessionEvent.Create(
            SessionType.Focus, id, 1, SessionEventType.Abandoned, Now, DateOnly.FromDateTime(Now), 0, atLimit, Now);

        Assert.Equal(2000, accepted.PayloadJson.Length);
        Assert.Equal(atLimit, accepted.PayloadJson);
        Assert.Throws<DomainException>(() => SessionEvent.Create(
            SessionType.Focus, id, 2, SessionEventType.Start, Now, DateOnly.FromDateTime(Now), 0, atLimit + "x", Now));
    }

    [Theory]
    [InlineData(SessionEventType.Interrupted)]
    [InlineData(SessionEventType.Abandoned)]
    public void Interrupted_and_abandoned_are_retained_event_types_that_can_be_created(SessionEventType eventType)
    {
        Assert.True(Enum.IsDefined(eventType));
        var occurredAt = Now;
        var record = SessionEvent.Create(
            SessionType.Focus, Guid.NewGuid(), 1, eventType, occurredAt, DateOnly.FromDateTime(occurredAt), 0, "", occurredAt);
        Assert.Equal(eventType, record.EventType);
    }

    // ---- SessionException (§4.8) ----

    [Fact]
    public void Session_exceptions_require_expiry_after_grant()
    {
        Assert.Throws<DomainException>(() => SessionException.Create(
            Guid.NewGuid(), ExceptionTargetType.Website, "example.com", Now, Now, ExceptionReasonCategory.StudyResource, Now));

        var exception = SessionException.Create(
            Guid.NewGuid(), ExceptionTargetType.Application, Guid.NewGuid().ToString(), Now, Now.AddMinutes(2), ExceptionReasonCategory.Emergency, Now);
        Assert.False(exception.WasAutoExpired);
        exception.MarkAutoExpired();
        Assert.True(exception.WasAutoExpired);
        exception.MarkAutoExpired(); // idempotent
        Assert.True(exception.WasAutoExpired);
    }

    // ---- UserAchievement (§4.14) ----

    [Fact]
    public void User_achievement_links_one_ledger_entry()
    {
        var ua = UserAchievement.Create(Guid.NewGuid(), "ACH-001", Now);
        Assert.Null(ua.LedgerEntryId);

        var entry = Guid.NewGuid();
        ua.AttachLedgerEntry(entry);
        Assert.Throws<DomainException>(() => ua.AttachLedgerEntry(Guid.NewGuid()));
        ua.AttachLedgerEntry(entry);

        ua.MarkSeen(Now);
        Assert.NotNull(ua.SeenAtUtc);
    }
}
