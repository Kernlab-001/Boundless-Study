namespace BoundlessStudy.Domain;

/// <summary>
/// App-owned application-rule identification. The value is fixed by the product
/// (A2, approved 2026-11-19) and is no longer supplied by callers. New focus
/// sessions always snapshot this exact value; historical sessions keep whatever
/// they recorded.
/// </summary>
public static class AppRules
{
    /// <summary>The fixed MVP application-rule version identifier.</summary>
    public const string Version = "APP-RULES-1";
}
