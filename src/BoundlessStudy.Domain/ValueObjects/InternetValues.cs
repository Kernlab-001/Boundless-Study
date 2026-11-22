using System.Text;

namespace BoundlessStudy.Domain.ValueObjects;

/// <summary>
/// A normalized domain for blocked/allowed site rules (domain-business-rules.md §7.2,
/// conceptual-data-model.md §4.10):
/// - lowercased, leading "www." stripped,
/// - scheme, path, query, and fragment rejected,
/// - valid hostname labels only (letters, digits, hyphens; punycode "xn--" accepted),
/// - at most 253 characters, no empty labels, no leading/trailing label hyphens,
/// - at least one dot (a registrable domain), except exact IPv4 addresses which may be matched exactly.
/// Internationalized names arrive already punycode-encoded from the UI layer.
/// </summary>
public readonly record struct NormalizedDomain
{
    public const int MaxLength = 253;

    public string Value { get; }

    private NormalizedDomain(string value)
    {
        Value = value;
    }

    public static NormalizedDomain Create(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            throw new DomainException("DOMAIN_REQUIRED", "A domain is required.");
        }

        var domain = input.Trim().ToLowerInvariant();

        if (domain.Contains("://", StringComparison.Ordinal) || domain.Contains('/', StringComparison.Ordinal)
            || domain.Contains(':', StringComparison.Ordinal) || domain.Contains('?', StringComparison.Ordinal)
            || domain.Contains('#', StringComparison.Ordinal))
        {
            throw new DomainException("DOMAIN_NOT_A_HOST", "Enter a bare domain (scheme, path, query, and fragment are not stored).");
        }

        // Strip a single leading www.
        if (domain.StartsWith("www.", StringComparison.Ordinal) && domain.Length > 4)
        {
            domain = domain[4..];
        }

        if (domain.Length > MaxLength)
        {
            throw new DomainException("DOMAIN_TOO_LONG", $"A domain may be at most {MaxLength} characters.");
        }

        var labels = domain.Split('.', StringSplitOptions.TrimEntries);

        var looksLikeIpv4 = domain.All(c => char.IsAsciiDigit(c) || c == '.') && labels.Length == 4;
        if (looksLikeIpv4)
        {
            // Exact IPv4 matching: every octet must be 0–255.
            if (!labels.All(l => byte.TryParse(l, out _)))
            {
                throw new DomainException("DOMAIN_IP_INVALID", "An IPv4 address must have four octets between 0 and 255.");
            }
        }
        else
        {
            if (labels.Length < 2)
            {
                throw new DomainException("DOMAIN_INCOMPLETE", "Enter a full domain, for example example.com.");
            }

            foreach (var label in labels)
            {
                if (label.Length is 0 or > 63)
                {
                    throw new DomainException("DOMAIN_LABEL_INVALID", "Each domain label must be 1–63 characters.");
                }

                if (label.StartsWith('-') || label.EndsWith('-'))
                {
                    throw new DomainException("DOMAIN_LABEL_HYPHEN", "Domain labels cannot start or end with a hyphen.");
                }

                foreach (var c in label)
                {
                    var isPunycodePrefix = label.StartsWith("xn--", StringComparison.Ordinal);
                    var isAsciiLetter = c is >= 'a' and <= 'z';
                    if (!isAsciiLetter && !char.IsAsciiDigit(c) && c != '-' && !(isPunycodePrefix && (c == '_' || c > sbyte.MaxValue)))
                    {
                        throw new DomainException("DOMAIN_CHAR_INVALID", "Domains may contain letters, digits, and hyphens only (IDN must be punycode).");
                    }
                }
            }
        }

        return new NormalizedDomain(domain);
    }

    public override string ToString() => Value;
}

/// <summary>
/// A normalized executable target for application rules: an optional canonical path
/// plus a required normalized executable name fallback (conceptual-data-model.md §4.9).
/// Path matching is case-insensitive on Windows.
/// </summary>
public readonly record struct ExecutableTarget
{
    public const int MaxPathLength = 1000;
    public const int MaxNameLength = 260;

    public string? NormalizedExecutablePath { get; }
    public string NormalizedExecutableName { get; }

    private ExecutableTarget(string? path, string name)
    {
        NormalizedExecutablePath = path;
        NormalizedExecutableName = name;
    }

    public static ExecutableTarget Create(string? executablePath, string? executableName)
    {
        var name = executableName?.Trim() ?? string.Empty;

        if (name.Length == 0 && string.IsNullOrWhiteSpace(executablePath))
        {
            throw new DomainException("EXECUTABLE_TARGET_REQUIRED", "At least one of the executable path or name must be present.");
        }

        if (name.Length == 0)
        {
            // Name is the required fallback: derive it from the path when absent.
            var file = executablePath!.Split('/', '\\').Last();
            name = file.Trim();
        }

        if (name.Length > MaxNameLength)
        {
            throw new DomainException("EXECUTABLE_NAME_TOO_LONG", $"The executable name may be at most {MaxNameLength} characters.");
        }

        if (name.Contains('/', StringComparison.Ordinal) || name.Contains('\\', StringComparison.Ordinal))
        {
            throw new DomainException("EXECUTABLE_NAME_NOT_A_NAME", "The executable name must not contain path separators.");
        }

        string? path = null;
        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            path = executablePath.Trim();
            if (path.Length > MaxPathLength)
            {
                throw new DomainException("EXECUTABLE_PATH_TOO_LONG", $"The executable path may be at most {MaxPathLength} characters.");
            }
        }

        return new ExecutableTarget(path, name.ToLowerInvariant());
    }
}

/// <summary>
/// The unique idempotency key of a ledger entry (domain-business-rules.md §8.10):
/// <c>&lt;sourceType&gt;:&lt;sourceId&gt;:&lt;currency&gt;</c> with the exact MVP forms.
/// </summary>
public readonly record struct IdempotencyKey
{
    public const int MaxLength = 200;
    public const int MaxSourceIdLength = 100;

    public string Value { get; }

    private IdempotencyKey(string value)
    {
        Value = value;
    }

    public static IdempotencyKey For(LedgerSourceType sourceType, string sourceId, CurrencyCode currency)
    {
        var key = $"{sourceType}:{sourceId}:{CurrencyText(currency)}";

        if (key.Length > MaxLength)
        {
            throw new DomainException("IDEMPOTENCY_KEY_TOO_LONG", "The idempotency key exceeds its maximum length.");
        }

        var segments = key.Split(':');
        if (segments.Length != 3
            || !Enum.TryParse<LedgerSourceType>(segments[0], out _)
            || segments[1].Length == 0
            || segments[1].Length > MaxSourceIdLength
            || (segments[2] != "XP" && segments[2] != "TOKEN"))
        {
            throw new DomainException("IDEMPOTENCY_KEY_INVALID", "The idempotency key does not have a valid form.");
        }

        return new IdempotencyKey(key);
    }

    public static string CurrencyText(CurrencyCode currency) => currency switch
    {
        CurrencyCode.Xp => "XP",
        CurrencyCode.Token => "TOKEN",
        _ => throw new DomainException("CURRENCY_UNKNOWN", "Unknown currency code."),
    };

    public override string ToString() => Value;
}
