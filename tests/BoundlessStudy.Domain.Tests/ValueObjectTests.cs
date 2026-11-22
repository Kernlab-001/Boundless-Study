using BoundlessStudy.Domain;
using BoundlessStudy.Domain.ValueObjects;

namespace BoundlessStudy.Domain.Tests;

public class ValueObjectTests
{
    [Fact]
    public void EstimateMinutes_bounds_and_steps()
    {
        Assert.Equal(5, EstimateMinutes.Min);
        Assert.Equal(600, EstimateMinutes.Max);
        Assert.Equal(5, EstimateMinutes.Step);

        Assert.Throws<DomainException>(() => EstimateMinutes.Create(0));
        Assert.Throws<DomainException>(() => EstimateMinutes.Create(601));
        Assert.Throws<DomainException>(() => EstimateMinutes.Create(12));

        Assert.Equal(600, EstimateMinutes.Create(600).Minutes);
    }

    [Fact]
    public void PlannedFocusSeconds_bounds_and_steps()
    {
        Assert.Equal(300, PlannedFocusSeconds.Min);
        Assert.Equal(10_800, PlannedFocusSeconds.Max);
        Assert.Equal(300, PlannedFocusSeconds.Step);

        Assert.Throws<DomainException>(() => PlannedFocusSeconds.Create(299));
        Assert.Throws<DomainException>(() => PlannedFocusSeconds.Create(10_801));
        Assert.Throws<DomainException>(() => PlannedFocusSeconds.Create(750)); // 12.5 min

        Assert.Equal(1_800, PlannedFocusSeconds.FromMinutes(30).Seconds);
    }

    [Fact]
    public void PlannedBreakSeconds_bounds()
    {
        Assert.Throws<DomainException>(() => PlannedBreakSeconds.Create(59));
        Assert.Throws<DomainException>(() => PlannedBreakSeconds.Create(3_601));
        // No step requirement is defined: 90 seconds is valid.
        Assert.Equal(90, PlannedBreakSeconds.Create(90).Seconds);
    }

    [Fact]
    public void NormalizedDomain_normalization()
    {
        Assert.Equal("example.com", NormalizedDomain.Create("WWW.EXAMPLE.com").Value);
        Assert.Equal("example.com", NormalizedDomain.Create("www.example.com").Value);
        Assert.Equal("example.com", NormalizedDomain.Create("  EXAMPLE.com  ").Value);
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("example.com/path")]
    [InlineData("example.com:8080")]
    [InlineData("example.com?q=1")]
    [InlineData("example.com#frag")]
    [InlineData("localhost")]
    [InlineData("a..b.com")]
    [InlineData("-bad.example.com")]
    [InlineData("bad.example-.com")]
    [InlineData("under_score.example.com")]
    public void NormalizedDomain_rejects_scheme_path_and_invalid_hosts(string input)
    {
        Assert.Throws<DomainException>(() => NormalizedDomain.Create(input));
    }

    [Fact]
    public void NormalizedDomain_rejects_empty_and_too_long()
    {
        Assert.Throws<DomainException>(() => NormalizedDomain.Create(""));
        Assert.Throws<DomainException>(() => NormalizedDomain.Create(new string('a', 250) + ".com"));
    }

    [Fact]
    public void NormalizedDomain_allows_exact_ipv4()
    {
        Assert.Equal("192.168.1.10", NormalizedDomain.Create("192.168.1.10").Value);
    }

    [Fact]
    public void ExecutableTarget_normalization_and_fallback()
    {
        var fromPath = ExecutableTarget.Create(@"C:\Program Files\App\App.EXE", null);
        Assert.Equal("app.exe", fromPath.NormalizedExecutableName);
        Assert.Equal(@"C:\Program Files\App\App.EXE", fromPath.NormalizedExecutablePath);

        var nameOnly = ExecutableTarget.Create(null, "  GAME.EXE  ");
        Assert.Equal("game.exe", nameOnly.NormalizedExecutableName);
        Assert.Null(nameOnly.NormalizedExecutablePath);

        Assert.Throws<DomainException>(() => ExecutableTarget.Create(null, null));
        Assert.Throws<DomainException>(() => ExecutableTarget.Create(null, "dir\\name.exe"));
        Assert.Throws<DomainException>(() => ExecutableTarget.Create(null, new string('a', 261)));
        Assert.Throws<DomainException>(() => ExecutableTarget.Create(new string('a', 1001), "name.exe"));
    }

    [Fact]
    public void IdempotencyKey_exact_mvp_forms()
    {
        var sessionId = Guid.NewGuid().ToString();
        Assert.Equal($"SessionCompletion:{sessionId}:XP",
            IdempotencyKey.For(LedgerSourceType.SessionCompletion, sessionId, CurrencyCode.Xp).Value);
        Assert.Equal($"SessionPartial:{sessionId}:XP",
            IdempotencyKey.For(LedgerSourceType.SessionPartial, sessionId, CurrencyCode.Xp).Value);
        Assert.Equal("TaskCompletion:abc:XP",
            IdempotencyKey.For(LedgerSourceType.TaskCompletion, "abc", CurrencyCode.Xp).Value);
        Assert.Equal("AchievementUnlock:ACH-001:XP",
            IdempotencyKey.For(LedgerSourceType.AchievementUnlock, "ACH-001", CurrencyCode.Xp).Value);
    }

    [Fact]
    public void IdempotencyKey_rejects_invalid_forms()
    {
        Assert.Throws<DomainException>(() => IdempotencyKey.For(LedgerSourceType.TaskCompletion, "", CurrencyCode.Xp));
        Assert.Throws<DomainException>(() => IdempotencyKey.For(LedgerSourceType.TaskCompletion, new string('a', 101), CurrencyCode.Xp));

        // TOKEN is reserved but valid in the key form (no MVP entries are created).
        Assert.Equal("Adjustment:x:TOKEN", IdempotencyKey.For(LedgerSourceType.Adjustment, "x", CurrencyCode.Token).Value);
    }

    [Fact]
    public void Sha256Hex_validation()
    {
        var valid = new string('0', 64);
        Assert.Equal(valid, Sha256Hex.Create(valid.ToUpperInvariant()).Value); // normalized lowercase
        Assert.Throws<DomainException>(() => Sha256Hex.Create(new string('g', 64)));
        Assert.Throws<DomainException>(() => Sha256Hex.Create(new string('0', 63)));
        Assert.Throws<DomainException>(() => Sha256Hex.Create(""));
    }
}
