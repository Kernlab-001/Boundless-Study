namespace BoundlessStudy.Bridge;

/// <summary>
/// Domain normalization and Declarative Net Request (DNR) rule generation
/// for Chrome and Edge Manifest V3 extensions (ADR-0004, BS-BRW-006).
/// </summary>
public static class BrowserRuleGenerator
{
    public const int BlockPriority = 1;
    public const int AllowPriority = 2;

    public const string DefaultBlockedRedirectPath = "/blocked.html";

    /// <summary>
    /// Normalizes a raw URL or domain string to a canonical domain format.
    /// Strips protocol schemes, credentials, port numbers, paths, query strings,
    /// leading 'www.' subdomains, and trailing periods or slashes.
    /// </summary>
    public static string NormalizeDomain(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var normalized = raw.Trim().ToLowerInvariant();

        // Strip http:// or https:// schemes
        if (normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["https://".Length..];
        }
        else if (normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["http://".Length..];
        }

        // Strip user info if present
        var atIdx = normalized.IndexOf('@');
        if (atIdx >= 0)
        {
            normalized = normalized[(atIdx + 1)..];
        }

        // Strip path, query, and fragment
        var slashIdx = normalized.IndexOf('/');
        if (slashIdx >= 0)
        {
            normalized = normalized[..slashIdx];
        }

        var queryIdx = normalized.IndexOf('?');
        if (queryIdx >= 0)
        {
            normalized = normalized[..queryIdx];
        }

        var fragmentIdx = normalized.IndexOf('#');
        if (fragmentIdx >= 0)
        {
            normalized = normalized[..fragmentIdx];
        }

        // Strip port if present
        var colonIdx = normalized.IndexOf(':');
        if (colonIdx >= 0)
        {
            normalized = normalized[..colonIdx];
        }

        // Strip leading www.
        if (normalized.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["www.".Length..];
        }

        return normalized.TrimEnd('.');
    }

    /// <summary>
    /// Formats a normalized domain into Chromium Declarative Net Request urlFilter format: ||domain^
    /// </summary>
    public static string ToUrlFilter(string? domain)
    {
        var normalized = NormalizeDomain(domain);
        if (string.IsNullOrEmpty(normalized))
        {
            return string.Empty;
        }

        return $"||{normalized}^";
    }

    /// <summary>
    /// Creates a block rule (priority 1) redirecting main_frame navigation to blocked.html.
    /// </summary>
    public static DnrRule CreateBlockRule(int ruleId, string domain, string? redirectPath = DefaultBlockedRedirectPath)
    {
        var urlFilter = ToUrlFilter(domain);
        var action = redirectPath is not null
            ? new DnrAction("redirect", new DnrRedirect(redirectPath))
            : new DnrAction("block");

        return new DnrRule(
            Id: ruleId,
            Priority: BlockPriority,
            Action: action,
            Condition: new DnrCondition(urlFilter, new[] { "main_frame" }));
    }

    /// <summary>
    /// Creates an allow rule (priority 2, taking precedence over block per BS-BRW-006).
    /// </summary>
    public static DnrRule CreateAllowRule(int ruleId, string domain)
    {
        var urlFilter = ToUrlFilter(domain);
        return new DnrRule(
            Id: ruleId,
            Priority: AllowPriority,
            Action: new DnrAction("allow"),
            Condition: new DnrCondition(urlFilter, new[] { "main_frame", "sub_frame" }));
    }

    /// <summary>
    /// Generates complete dynamic rules for a policy: blocked domains at priority 1, allowed domains at priority 2.
    /// Rule IDs start at startingRuleId and increment sequentially.
    /// </summary>
    public static IReadOnlyList<DnrRule> GenerateRules(
        IEnumerable<string> blockedDomains,
        IEnumerable<string> allowedDomains,
        int startingRuleId = 1000)
    {
        var rules = new List<DnrRule>();
        var currentId = startingRuleId;

        // Blocked domains: priority 1
        foreach (var blocked in blockedDomains)
        {
            var filter = ToUrlFilter(blocked);
            if (!string.IsNullOrEmpty(filter))
            {
                rules.Add(CreateBlockRule(currentId++, blocked));
            }
        }

        // Allowed domains: priority 2 (allow takes precedence per BS-BRW-006)
        foreach (var allowed in allowedDomains)
        {
            var filter = ToUrlFilter(allowed);
            if (!string.IsNullOrEmpty(filter))
            {
                rules.Add(CreateAllowRule(currentId++, allowed));
            }
        }

        return rules;
    }
}

public sealed record DnrRule(
    int Id,
    int Priority,
    DnrAction Action,
    DnrCondition Condition);

public sealed record DnrAction(
    string Type,
    DnrRedirect? Redirect = null);

public sealed record DnrRedirect(
    string ExtensionPath);

public sealed record DnrCondition(
    string UrlFilter,
    IReadOnlyList<string> ResourceTypes);
