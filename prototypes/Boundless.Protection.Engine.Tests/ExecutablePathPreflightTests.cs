using Boundless.Protection.Engine;

namespace Boundless.Protection.Engine.Tests;

public sealed class ExecutablePathPreflightTests
{
    [Theory]
    [InlineData("C:\\BPE-Synthetic\\reader.exe")]
    [InlineData("D:\\fixtures\\app.exe")]
    public void OrdinaryDriveRootedPathStillRequiresOsVerification(string path)
    {
        var result = ExecutablePathPreflight.Inspect(path);

        Assert.Equal(ExecutablePathPreflightStatus.LexicallyValidButUnverified, result.Status);
        Assert.True(result.RequiresOperatingSystemIdentityResolution);
        Assert.False(result.CanUseForTrustedDecision);
    }

    [Theory]
    [InlineData("C:\\BPE-Synthetic\\PROGRA~1\\reader.exe")]
    [InlineData("C:\\BPE-Synthetic\\reader.exe ")]
    [InlineData("C:\\BPE-Synthetic\\reader.exe.")]
    [InlineData("C:\\BPE-Synthetic\\reader.exe:stream")]
    [InlineData("C:\\BPE-Synthetic\\NUL.exe")]
    [InlineData("C:\\BPE-Synthetic\\CONIN$.exe")]
    [InlineData("C:\\BPE-Synthetic\\CONOUT$.exe")]
    public void KnownAmbiguousAliasesAreRejected(string path)
    {
        var result = ExecutablePathPreflight.Inspect(path);

        Assert.Equal(ExecutablePathPreflightStatus.AmbiguousAlias, result.Status);
        Assert.False(result.CanUseForTrustedDecision);
    }

    [Theory]
    [InlineData("\\\\?\\C:\\BPE-Synthetic\\reader.exe")]
    [InlineData("\\\\.\\C:\\BPE-Synthetic\\reader.exe")]
    [InlineData("\\??\\C:\\BPE-Synthetic\\reader.exe")]
    public void DeviceAndExtendedNamespacesAreUnsupported(string path)
    {
        var result = ExecutablePathPreflight.Inspect(path);

        Assert.Equal(ExecutablePathPreflightStatus.UnsupportedNamespace, result.Status);
    }

    [Theory]
    [InlineData("\\\\server\\share\\reader.exe")]
    [InlineData("relative\\reader.exe")]
    [InlineData("C:reader.exe")]
    public void NonDriveRootedNamesAreUnsupported(string path)
    {
        var result = ExecutablePathPreflight.Inspect(path);

        Assert.Equal(ExecutablePathPreflightStatus.UnsupportedRoot, result.Status);
    }

    [Theory]
    [InlineData("C:\\BPE-Synthetic\\..\\reader.exe")]
    [InlineData("C:\\BPE-Synthetic\\\\reader.exe")]
    [InlineData("C:\\BPE-Synthetic\\")]
    public void TraversalAndEmptySegmentsAreInvalid(string path)
    {
        var result = ExecutablePathPreflight.Inspect(path);

        Assert.Equal(ExecutablePathPreflightStatus.Invalid, result.Status);
    }

    [Fact]
    public void AliasChecksDoNotClaimToDetectFilesystemOrWow64Aliases()
    {
        var result = ExecutablePathPreflight.Inspect("C:\\Windows\\System32\\notepad.exe");

        Assert.True(result.RequiresOperatingSystemIdentityResolution);
        Assert.False(result.CanUseForTrustedDecision);
        Assert.Equal("requires-os-identity-resolution", result.ReasonCode);
    }

    [Fact]
    public void ProtectedCatalogRejectsUncWindowsDirectory()
    {
        Assert.Throws<ArgumentException>(() => ProtectedTargetCatalog.ForWindowsDirectory("\\\\server\\share\\Windows"));
    }
}
