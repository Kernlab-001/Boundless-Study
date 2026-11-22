using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BoundlessStudy.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AchievementDefinition",
                columns: table => new
                {
                    Code = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    CriteriaType = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Threshold = table.Column<int>(type: "INTEGER", nullable: false),
                    XpAward = table.Column<int>(type: "INTEGER", nullable: false),
                    EconomyRuleVersion = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AchievementDefinition", x => x.Code);
                    table.CheckConstraint("CK_AchievementDefinition_Code", "length(trim(\"Code\")) BETWEEN 1 AND 40");
                    table.CheckConstraint("CK_AchievementDefinition_CriteriaType", "\"CriteriaType\" IN ('SessionCount', 'ActiveMinutes', 'StreakDays', 'StrictSessionCount', 'TaskCount')");
                    table.CheckConstraint("CK_AchievementDefinition_Description", "length(\"Description\") <= 500");
                    table.CheckConstraint("CK_AchievementDefinition_Name", "length(trim(\"Name\")) BETWEEN 1 AND 100");
                    table.CheckConstraint("CK_AchievementDefinition_Threshold", "\"Threshold\" > 0");
                    table.CheckConstraint("CK_AchievementDefinition_XpAward", "\"XpAward\" > 0");
                });

            migrationBuilder.CreateTable(
                name: "AppSetting",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ValueJson = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppSetting", x => x.Key);
                    table.CheckConstraint("CK_AppSetting_Key", "length(trim(\"Key\")) BETWEEN 1 AND 100");
                    table.CheckConstraint("CK_AppSetting_ValueJson", "json_valid(\"ValueJson\")");
                });

            migrationBuilder.CreateTable(
                name: "FocusProfile",
                columns: table => new
                {
                    Code = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    EarlyExitMode = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    EarlyExitCooldownSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxExceptionsPerSession = table.Column<int>(type: "INTEGER", nullable: false),
                    ExceptionDurationSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    ExceptionDelaySeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    XpPerActiveMinute = table.Column<int>(type: "INTEGER", nullable: false),
                    RequiresBrowserWhenSiteRulesExist = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    EconomyRuleVersion = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FocusProfile", x => x.Code);
                    table.CheckConstraint("CK_FocusProfile_Code", "length(trim(\"Code\")) BETWEEN 1 AND 20");
                    table.CheckConstraint("CK_FocusProfile_DisplayName", "length(trim(\"DisplayName\")) BETWEEN 1 AND 40");
                    table.CheckConstraint("CK_FocusProfile_EarlyExitMode", "\"EarlyExitMode\" IN ('Immediate', 'ConfirmationCooldown', 'TypedConfirmationCooldown')");
                    table.CheckConstraint("CK_FocusProfile_Timings", "\"EarlyExitCooldownSeconds\" >= 0 AND \"MaxExceptionsPerSession\" >= 0 AND \"ExceptionDurationSeconds\" >= 0 AND \"ExceptionDelaySeconds\" >= 0");
                    table.CheckConstraint("CK_FocusProfile_XpRate", "\"XpPerActiveMinute\" > 0");
                });

            migrationBuilder.CreateTable(
                name: "LocalProfile",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastOpenedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CachedTotalXp = table.Column<long>(type: "INTEGER", nullable: false),
                    CachedLevel = table.Column<int>(type: "INTEGER", nullable: false),
                    EconomyRuleVersion = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    IsOnboarded = table.Column<bool>(type: "INTEGER", nullable: false),
                    InstallationMode = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LocalProfile", x => x.Id);
                    table.CheckConstraint("CK_LocalProfile_CachedLevel", "\"CachedLevel\" >= 1");
                    table.CheckConstraint("CK_LocalProfile_CachedTotalXp", "\"CachedTotalXp\" >= 0");
                    table.CheckConstraint("CK_LocalProfile_DisplayName", "length(trim(\"DisplayName\")) BETWEEN 1 AND 80");
                    table.CheckConstraint("CK_LocalProfile_InstallationMode", "\"InstallationMode\" IN ('Full', 'Limited')");
                });

            migrationBuilder.CreateTable(
                name: "SessionEvent",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SessionType = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    EventType = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LocalDateAtEvent = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    UtcOffsetMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SessionEvent", x => x.Id);
                    table.CheckConstraint("CK_SessionEvent_EventType", "\"EventType\" IN ('Start', 'Blocked', 'Unblocked', 'IdleStarted', 'IdleEnded', 'SystemPaused', 'SystemResumed', 'BreakStarted', 'BreakEnded', 'ExceptionGranted', 'ProtectionDegraded', 'ProtectionRestored', 'Recovered', 'Completed', 'EndedEarly', 'Interrupted', 'Abandoned', 'EnforcementFailed', 'RewardCreated', 'AchievementUnlocked')");
                    table.CheckConstraint("CK_SessionEvent_PayloadJson", "length(\"PayloadJson\") <= 2000");
                    table.CheckConstraint("CK_SessionEvent_Sequence", "\"Sequence\" >= 1");
                    table.CheckConstraint("CK_SessionEvent_SessionType", "\"SessionType\" IN ('Focus', 'Break')");
                });

            migrationBuilder.CreateTable(
                name: "AllowedSite",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    LocalProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    NormalizedDomain = table.Column<string>(type: "TEXT", maxLength: 253, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 253, nullable: true),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AllowedSite", x => x.Id);
                    table.CheckConstraint("CK_AllowedSite_DisplayName", "\"DisplayName\" IS NULL OR length(\"DisplayName\") <= 253");
                    table.CheckConstraint("CK_AllowedSite_Domain", "length(trim(\"NormalizedDomain\")) BETWEEN 1 AND 253");
                    table.ForeignKey(
                        name: "FK_AllowedSite_LocalProfile_LocalProfileId",
                        column: x => x.LocalProfileId,
                        principalTable: "LocalProfile",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BackupRecord",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    LocalProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 260, nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    SchemaVersion = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    AppVersion = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    VerifiedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackupRecord", x => x.Id);
                    table.CheckConstraint("CK_BackupRecord_FileName", "length(trim(\"FileName\")) BETWEEN 1 AND 260");
                    table.CheckConstraint("CK_BackupRecord_Reason", "\"Reason\" IN ('Manual', 'Automatic', 'PreMigration', 'PreRestore')");
                    table.CheckConstraint("CK_BackupRecord_Sha256", "length(\"Sha256\") = 64");
                    table.CheckConstraint("CK_BackupRecord_SizeBytes", "\"SizeBytes\" >= 0");
                    table.ForeignKey(
                        name: "FK_BackupRecord_LocalProfile_LocalProfileId",
                        column: x => x.LocalProfileId,
                        principalTable: "LocalProfile",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BlockedApp",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    LocalProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    NormalizedExecutablePath = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    NormalizedExecutableName = table.Column<string>(type: "TEXT", maxLength: 260, nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BlockedApp", x => x.Id);
                    table.CheckConstraint("CK_BlockedApp_DisplayName", "length(trim(\"DisplayName\")) BETWEEN 1 AND 200");
                    table.CheckConstraint("CK_BlockedApp_ExecutableName", "length(trim(\"NormalizedExecutableName\")) BETWEEN 1 AND 260");
                    table.CheckConstraint("CK_BlockedApp_Path", "\"NormalizedExecutablePath\" IS NULL OR length(\"NormalizedExecutablePath\") <= 1000");
                    table.ForeignKey(
                        name: "FK_BlockedApp_LocalProfile_LocalProfileId",
                        column: x => x.LocalProfileId,
                        principalTable: "LocalProfile",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BlockedSite",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    LocalProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    NormalizedDomain = table.Column<string>(type: "TEXT", maxLength: 253, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 253, nullable: true),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BlockedSite", x => x.Id);
                    table.CheckConstraint("CK_BlockedSite_DisplayName", "\"DisplayName\" IS NULL OR length(\"DisplayName\") <= 253");
                    table.CheckConstraint("CK_BlockedSite_Domain", "length(trim(\"NormalizedDomain\")) BETWEEN 1 AND 253");
                    table.ForeignKey(
                        name: "FK_BlockedSite_LocalProfile_LocalProfileId",
                        column: x => x.LocalProfileId,
                        principalTable: "LocalProfile",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RewardLedgerEntry",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    LocalProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CurrencyCode = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    Amount = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceType = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    SourceId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    AwardedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AwardLocalDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    UtcOffsetMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    EconomyRuleVersion = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RewardLedgerEntry", x => x.Id);
                    table.CheckConstraint("CK_RewardLedgerEntry_Amount", "\"Amount\" > 0");
                    table.CheckConstraint("CK_RewardLedgerEntry_Currency", "\"CurrencyCode\" IN ('XP', 'TOKEN')");
                    table.CheckConstraint("CK_RewardLedgerEntry_IdempotencyKey", "length(trim(\"IdempotencyKey\")) BETWEEN 1 AND 200");
                    table.CheckConstraint("CK_RewardLedgerEntry_Notes", "\"Notes\" IS NULL OR length(\"Notes\") <= 500");
                    table.CheckConstraint("CK_RewardLedgerEntry_SourceId", "length(trim(\"SourceId\")) BETWEEN 1 AND 100");
                    table.CheckConstraint("CK_RewardLedgerEntry_SourceType", "\"SourceType\" IN ('SessionCompletion', 'SessionPartial', 'TaskCompletion', 'AchievementUnlock', 'Adjustment')");
                    table.ForeignKey(
                        name: "FK_RewardLedgerEntry_LocalProfile_LocalProfileId",
                        column: x => x.LocalProfileId,
                        principalTable: "LocalProfile",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Subject",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    LocalProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    IsArchived = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Subject", x => x.Id);
                    table.CheckConstraint("CK_Subject_Description", "length(\"Description\") <= 500");
                    table.CheckConstraint("CK_Subject_Name", "length(trim(\"Name\")) BETWEEN 1 AND 100");
                    table.ForeignKey(
                        name: "FK_Subject_LocalProfile_LocalProfileId",
                        column: x => x.LocalProfileId,
                        principalTable: "LocalProfile",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserAchievement",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    LocalProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AchievementCode = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    UnlockedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LedgerEntryId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SeenAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserAchievement", x => x.Id);
                    table.CheckConstraint("CK_UserAchievement_Code", "length(trim(\"AchievementCode\")) BETWEEN 1 AND 40");
                    table.ForeignKey(
                        name: "FK_UserAchievement_AchievementDefinition_AchievementCode",
                        column: x => x.AchievementCode,
                        principalTable: "AchievementDefinition",
                        principalColumn: "Code",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UserAchievement_LocalProfile_LocalProfileId",
                        column: x => x.LocalProfileId,
                        principalTable: "LocalProfile",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserAchievement_RewardLedgerEntry_LedgerEntryId",
                        column: x => x.LedgerEntryId,
                        principalTable: "RewardLedgerEntry",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "StudyTask",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    LocalProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SubjectId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    DueDateLocal = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    EstimatedMinutes = table.Column<int>(type: "INTEGER", nullable: true),
                    Priority = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    PlannedForLocalDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletionRewardId = table.Column<Guid>(type: "TEXT", nullable: true),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    DeletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StudyTask", x => x.Id);
                    table.CheckConstraint("CK_StudyTask_CompletedAt", "\"Status\" <> 'Done' OR \"CompletedAtUtc\" IS NOT NULL");
                    table.CheckConstraint("CK_StudyTask_EstimatedMinutes", "\"EstimatedMinutes\" IS NULL OR (\"EstimatedMinutes\" BETWEEN 5 AND 600 AND \"EstimatedMinutes\" % 5 = 0)");
                    table.CheckConstraint("CK_StudyTask_Notes", "length(\"Notes\") <= 4000");
                    table.CheckConstraint("CK_StudyTask_Priority", "\"Priority\" IN ('Low', 'Normal', 'High')");
                    table.CheckConstraint("CK_StudyTask_SoftDelete", "\"IsDeleted\" = 0 OR \"DeletedAtUtc\" IS NOT NULL");
                    table.CheckConstraint("CK_StudyTask_Status", "\"Status\" IN ('Todo', 'InProgress', 'Done', 'Archived')");
                    table.CheckConstraint("CK_StudyTask_Title", "length(trim(\"Title\")) BETWEEN 1 AND 200");
                    table.ForeignKey(
                        name: "FK_StudyTask_LocalProfile_LocalProfileId",
                        column: x => x.LocalProfileId,
                        principalTable: "LocalProfile",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_StudyTask_RewardLedgerEntry_CompletionRewardId",
                        column: x => x.CompletionRewardId,
                        principalTable: "RewardLedgerEntry",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StudyTask_Subject_SubjectId",
                        column: x => x.SubjectId,
                        principalTable: "Subject",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FocusSession",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    LocalProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TaskId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SubjectId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TaskTitleSnapshot = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    SubjectNameSnapshot = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ProfileCode = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    PlannedFocusSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastCheckpointAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ElapsedSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    ActiveStudySeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    IdleSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    ProtectionGapSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    ViolationCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ExceptionCount = table.Column<int>(type: "INTEGER", nullable: false),
                    RecoveryCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ProtectionStatus = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    VerificationStatus = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    EndReason = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    AppRuleVersion = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    EconomyRuleVersion = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FocusSession", x => x.Id);
                    table.CheckConstraint("CK_FocusSession_Abandoned", "\"State\" <> 'Abandoned' OR (\"EndedAtUtc\" IS NOT NULL AND \"EndReason\" IS NOT NULL AND \"EndReason\" <> 'Completed')");
                    table.CheckConstraint("CK_FocusSession_ActiveWithinElapsed", "\"ActiveStudySeconds\" <= \"ElapsedSeconds\"");
                    table.CheckConstraint("CK_FocusSession_Completed", "\"State\" <> 'Completed' OR (\"EndedAtUtc\" IS NOT NULL AND \"EndReason\" = 'Completed' AND \"ElapsedSeconds\" >= \"PlannedFocusSeconds\")");
                    table.CheckConstraint("CK_FocusSession_Counters", "\"ElapsedSeconds\" >= 0 AND \"ActiveStudySeconds\" >= 0 AND \"IdleSeconds\" >= 0 AND \"ProtectionGapSeconds\" >= 0 AND \"ViolationCount\" >= 0 AND \"ExceptionCount\" >= 0 AND \"RecoveryCount\" >= 0");
                    table.CheckConstraint("CK_FocusSession_EndedEarly", "\"State\" <> 'EndedEarly' OR (\"EndedAtUtc\" IS NOT NULL AND \"EndReason\" IS NOT NULL AND \"EndReason\" <> 'Completed')");
                    table.CheckConstraint("CK_FocusSession_EndReason", "\"EndReason\" IS NULL OR \"EndReason\" IN ('Completed', 'UserEnded', 'AgentCrash', 'Shutdown', 'PowerLoss', 'OverlayFailure', 'EnforcementFailure', 'Unknown')");
                    table.CheckConstraint("CK_FocusSession_PlannedFocusSeconds", "\"PlannedFocusSeconds\" BETWEEN 300 AND 10800 AND \"PlannedFocusSeconds\" % 300 = 0");
                    table.CheckConstraint("CK_FocusSession_ProtectionStatus", "\"ProtectionStatus\" IN ('NotRequired', 'NotConfigured', 'Protected', 'Degraded', 'Unavailable', 'Unknown')");
                    table.CheckConstraint("CK_FocusSession_State", "\"State\" IN ('Active', 'Blocked', 'SystemPaused', 'Completed', 'EndedEarly', 'RecoverableInterrupted', 'Abandoned')");
                    table.CheckConstraint("CK_FocusSession_VerificationStatus", "\"VerificationStatus\" IN ('Verified', 'Degraded', 'Unverified')");
                    table.ForeignKey(
                        name: "FK_FocusSession_FocusProfile_ProfileCode",
                        column: x => x.ProfileCode,
                        principalTable: "FocusProfile",
                        principalColumn: "Code",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FocusSession_LocalProfile_LocalProfileId",
                        column: x => x.LocalProfileId,
                        principalTable: "LocalProfile",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_FocusSession_StudyTask_TaskId",
                        column: x => x.TaskId,
                        principalTable: "StudyTask",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_FocusSession_Subject_SubjectId",
                        column: x => x.SubjectId,
                        principalTable: "Subject",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "BreakSession",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    LocalProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FocusSessionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    PlannedSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ElapsedSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    EndReason = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BreakSession", x => x.Id);
                    table.CheckConstraint("CK_BreakSession_Completed", "\"State\" <> 'Completed' OR (\"EndedAtUtc\" IS NOT NULL AND \"EndReason\" = 'Completed')");
                    table.CheckConstraint("CK_BreakSession_ElapsedSeconds", "\"ElapsedSeconds\" >= 0");
                    table.CheckConstraint("CK_BreakSession_EndedEarly", "\"State\" <> 'EndedEarly' OR (\"EndedAtUtc\" IS NOT NULL AND \"EndReason\" IS NOT NULL AND \"EndReason\" <> 'Completed')");
                    table.CheckConstraint("CK_BreakSession_EndReason", "\"EndReason\" IS NULL OR \"EndReason\" IN ('Completed', 'UserEnded', 'LockOrSleepTimeout', 'Shutdown', 'Unknown')");
                    table.CheckConstraint("CK_BreakSession_PlannedSeconds", "\"PlannedSeconds\" BETWEEN 60 AND 3600");
                    table.CheckConstraint("CK_BreakSession_State", "\"State\" IN ('Active', 'Completed', 'EndedEarly', 'Interrupted')");
                    table.ForeignKey(
                        name: "FK_BreakSession_FocusSession_FocusSessionId",
                        column: x => x.FocusSessionId,
                        principalTable: "FocusSession",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_BreakSession_LocalProfile_LocalProfileId",
                        column: x => x.LocalProfileId,
                        principalTable: "LocalProfile",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SessionException",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    FocusSessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TargetType = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    TargetId = table.Column<string>(type: "TEXT", maxLength: 253, nullable: false),
                    GrantedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ReasonCategory = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    WasAutoExpired = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SessionException", x => x.Id);
                    table.CheckConstraint("CK_SessionException_Expiry", "\"ExpiresAtUtc\" > \"GrantedAtUtc\"");
                    table.CheckConstraint("CK_SessionException_ReasonCategory", "\"ReasonCategory\" IN ('Technical', 'Accessibility', 'StudyResource', 'Emergency', 'Other')");
                    table.CheckConstraint("CK_SessionException_TargetId", "length(trim(\"TargetId\")) BETWEEN 1 AND 253");
                    table.CheckConstraint("CK_SessionException_TargetType", "\"TargetType\" IN ('Application', 'Website')");
                    table.ForeignKey(
                        name: "FK_SessionException_FocusSession_FocusSessionId",
                        column: x => x.FocusSessionId,
                        principalTable: "FocusSession",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AllowedSite_LocalProfileId_IsEnabled",
                table: "AllowedSite",
                columns: new[] { "LocalProfileId", "IsEnabled" });

            migrationBuilder.CreateIndex(
                name: "IX_AllowedSite_LocalProfileId_NormalizedDomain",
                table: "AllowedSite",
                columns: new[] { "LocalProfileId", "NormalizedDomain" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BackupRecord_LocalProfileId_CreatedAtUtc",
                table: "BackupRecord",
                columns: new[] { "LocalProfileId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_BlockedApp_LocalProfileId_IsEnabled",
                table: "BlockedApp",
                columns: new[] { "LocalProfileId", "IsEnabled" });

            migrationBuilder.CreateIndex(
                name: "IX_BlockedApp_LocalProfileId_NormalizedExecutableName",
                table: "BlockedApp",
                columns: new[] { "LocalProfileId", "NormalizedExecutableName" });

            migrationBuilder.CreateIndex(
                name: "IX_BlockedSite_LocalProfileId_IsEnabled",
                table: "BlockedSite",
                columns: new[] { "LocalProfileId", "IsEnabled" });

            migrationBuilder.CreateIndex(
                name: "IX_BlockedSite_LocalProfileId_NormalizedDomain",
                table: "BlockedSite",
                columns: new[] { "LocalProfileId", "NormalizedDomain" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BreakSession_FocusSessionId",
                table: "BreakSession",
                column: "FocusSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_BreakSession_LocalProfileId_State",
                table: "BreakSession",
                columns: new[] { "LocalProfileId", "State" });

            migrationBuilder.CreateIndex(
                name: "UX_BreakSession_OneActivePerProfile",
                table: "BreakSession",
                column: "LocalProfileId",
                unique: true,
                filter: "\"State\" = 'Active'");

            migrationBuilder.CreateIndex(
                name: "IX_FocusSession_LocalProfileId_EndedAtUtc",
                table: "FocusSession",
                columns: new[] { "LocalProfileId", "EndedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_FocusSession_LocalProfileId_State",
                table: "FocusSession",
                columns: new[] { "LocalProfileId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_FocusSession_ProfileCode",
                table: "FocusSession",
                column: "ProfileCode");

            migrationBuilder.CreateIndex(
                name: "IX_FocusSession_SubjectId",
                table: "FocusSession",
                column: "SubjectId");

            migrationBuilder.CreateIndex(
                name: "IX_FocusSession_TaskId",
                table: "FocusSession",
                column: "TaskId");

            migrationBuilder.CreateIndex(
                name: "UX_FocusSession_OneActivePerProfile",
                table: "FocusSession",
                column: "LocalProfileId",
                unique: true,
                filter: "\"State\" IN ('Active', 'Blocked', 'SystemPaused', 'RecoverableInterrupted')");

            migrationBuilder.CreateIndex(
                name: "IX_RewardLedgerEntry_IdempotencyKey",
                table: "RewardLedgerEntry",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RewardLedgerEntry_LocalProfileId_AwardedAtUtc",
                table: "RewardLedgerEntry",
                columns: new[] { "LocalProfileId", "AwardedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_RewardLedgerEntry_LocalProfileId_AwardLocalDate_CurrencyCode",
                table: "RewardLedgerEntry",
                columns: new[] { "LocalProfileId", "AwardLocalDate", "CurrencyCode" });

            migrationBuilder.CreateIndex(
                name: "IX_SessionEvent_SessionId_Sequence",
                table: "SessionEvent",
                columns: new[] { "SessionId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SessionEvent_SessionType_SessionId",
                table: "SessionEvent",
                columns: new[] { "SessionType", "SessionId" });

            migrationBuilder.CreateIndex(
                name: "IX_SessionException_FocusSessionId",
                table: "SessionException",
                column: "FocusSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_StudyTask_CompletionRewardId",
                table: "StudyTask",
                column: "CompletionRewardId");

            migrationBuilder.CreateIndex(
                name: "IX_StudyTask_LocalProfileId_PlannedForLocalDate",
                table: "StudyTask",
                columns: new[] { "LocalProfileId", "PlannedForLocalDate" });

            migrationBuilder.CreateIndex(
                name: "IX_StudyTask_LocalProfileId_Status_DueDateLocal",
                table: "StudyTask",
                columns: new[] { "LocalProfileId", "Status", "DueDateLocal" });

            migrationBuilder.CreateIndex(
                name: "IX_StudyTask_SubjectId",
                table: "StudyTask",
                column: "SubjectId");

            migrationBuilder.CreateIndex(
                name: "IX_Subject_LocalProfileId_IsArchived",
                table: "Subject",
                columns: new[] { "LocalProfileId", "IsArchived" });

            migrationBuilder.CreateIndex(
                name: "IX_UserAchievement_AchievementCode",
                table: "UserAchievement",
                column: "AchievementCode");

            migrationBuilder.CreateIndex(
                name: "IX_UserAchievement_LedgerEntryId",
                table: "UserAchievement",
                column: "LedgerEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_UserAchievement_LocalProfileId_AchievementCode",
                table: "UserAchievement",
                columns: new[] { "LocalProfileId", "AchievementCode" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AllowedSite");

            migrationBuilder.DropTable(
                name: "AppSetting");

            migrationBuilder.DropTable(
                name: "BackupRecord");

            migrationBuilder.DropTable(
                name: "BlockedApp");

            migrationBuilder.DropTable(
                name: "BlockedSite");

            migrationBuilder.DropTable(
                name: "BreakSession");

            migrationBuilder.DropTable(
                name: "SessionEvent");

            migrationBuilder.DropTable(
                name: "SessionException");

            migrationBuilder.DropTable(
                name: "UserAchievement");

            migrationBuilder.DropTable(
                name: "FocusSession");

            migrationBuilder.DropTable(
                name: "AchievementDefinition");

            migrationBuilder.DropTable(
                name: "FocusProfile");

            migrationBuilder.DropTable(
                name: "StudyTask");

            migrationBuilder.DropTable(
                name: "RewardLedgerEntry");

            migrationBuilder.DropTable(
                name: "Subject");

            migrationBuilder.DropTable(
                name: "LocalProfile");
        }
    }
}
