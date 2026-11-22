using System.Text.Json;

namespace BoundlessStudy.Domain.Entities;

/// <summary>
/// A validated application setting (conceptual-data-model.md §4.15). The value is
/// validated JSON. Secrets are not stored here.
/// </summary>
public class AppSetting
{
    public const int MaxKeyLength = 100;

    private static readonly JsonDocumentOptions ValidationOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
    };

    public string Key { get; private set; } = string.Empty;

    public string ValueJson { get; private set; } = string.Empty;

    public DateTime UpdatedAtUtc { get; private set; }

    private AppSetting()
    {
    }

    public static AppSetting Create(string key, string valueJson, DateTime updatedAtUtc)
    {
        var k = (key ?? string.Empty).Trim();
        if (k.Length == 0 || k.Length > MaxKeyLength)
        {
            throw new DomainException("SETTING_KEY_INVALID", $"A setting key must be 1–{MaxKeyLength} characters.");
        }

        var value = ValidateJson(valueJson);
        return new AppSetting { Key = k, ValueJson = value, UpdatedAtUtc = updatedAtUtc };
    }

    public void Update(string valueJson, DateTime updatedAtUtc)
    {
        ValueJson = ValidateJson(valueJson);
        UpdatedAtUtc = updatedAtUtc;
    }

    private static string ValidateJson(string valueJson)
    {
        if (string.IsNullOrWhiteSpace(valueJson))
        {
            throw new DomainException("SETTING_JSON_INVALID", "A setting value must be valid JSON.");
        }

        try
        {
            using var _ = JsonDocument.Parse(valueJson, ValidationOptions);
        }
        catch (JsonException ex)
        {
            throw new DomainException("SETTING_JSON_INVALID", $"A setting value must be valid JSON: {ex.Message}");
        }

        return valueJson;
    }
}

/// <summary>
/// A backup record (conceptual-data-model.md §4.16). Records may be reconstructed
/// by scanning the backup directory if the database itself is lost.
/// </summary>
public class BackupRecord
{
    public const int MaxFileNameLength = 260;

    public Guid Id { get; private set; }

    public Guid LocalProfileId { get; private set; }

    public string FileName { get; private set; } = string.Empty;

    public BackupReason Reason { get; private set; }

    public string SchemaVersion { get; private set; } = string.Empty;

    public string AppVersion { get; private set; } = string.Empty;

    public string Sha256 { get; private set; } = string.Empty;

    public long SizeBytes { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    public DateTime? VerifiedAtUtc { get; private set; }

    private BackupRecord()
    {
    }

    public static BackupRecord Create(
        Guid localProfileId,
        string fileName,
        BackupReason reason,
        string schemaVersion,
        string appVersion,
        string sha256,
        long sizeBytes,
        DateTime createdAtUtc)
    {
        var file = (fileName ?? string.Empty).Trim();
        if (file.Length == 0 || file.Length > MaxFileNameLength)
        {
            throw new DomainException("BACKUP_FILE_NAME_INVALID", $"A backup file name must be 1–{MaxFileNameLength} characters.");
        }

        if (string.IsNullOrWhiteSpace(schemaVersion) || string.IsNullOrWhiteSpace(appVersion))
        {
            throw new DomainException("BACKUP_VERSIONS_REQUIRED", "A backup record must store the schema and application versions.");
        }

        var checksum = ValueObjects.Sha256Hex.Create(sha256);

        if (sizeBytes < 0)
        {
            throw new DomainException("BACKUP_SIZE_INVALID", "A backup size cannot be negative.");
        }

        return new BackupRecord
        {
            Id = Guid.NewGuid(),
            LocalProfileId = localProfileId,
            FileName = file,
            Reason = reason,
            SchemaVersion = schemaVersion,
            AppVersion = appVersion,
            Sha256 = checksum.Value,
            SizeBytes = sizeBytes,
            CreatedAtUtc = createdAtUtc,
        };
    }

    public void MarkVerified(DateTime verifiedAtUtc)
    {
        VerifiedAtUtc = verifiedAtUtc;
    }
}
