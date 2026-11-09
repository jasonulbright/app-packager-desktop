using System.Security.Cryptography;
using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The content prep tool is downloaded from a tagged release of the Microsoft repository, not from the moving master branch,
/// and the file must match the SHA-256 of that release before the Authenticode check and before it is installed.
/// </summary>
public sealed class IntuneWinToolPinTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "apkg-intunewin-pin-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, true); } catch (IOException) { }
    }

    [Fact]
    public void TheDefaultDownloadUrlNamesATagNotABranch()
    {
        var url = IntuneWinToolService.DefaultDownloadUrl;

        Assert.Matches(new Regex(@"^https://github\.com/microsoft/Microsoft-Win32-Content-Prep-Tool/raw/v\d+\.\d+\.\d+/IntuneWinAppUtil\.exe$"), url);
        Assert.DoesNotContain("/master/", url);
        Assert.DoesNotContain("/main/", url);
        Assert.Contains($"/raw/v{IntuneWinToolService.PinnedVersion}/", url);
    }

    [Fact]
    public void ThePinnedHashIsASha256Digest() =>
        Assert.Matches("^[0-9A-F]{64}$", IntuneWinToolService.PinnedSha256);

    [Fact]
    public async Task AFileWithAnotherHashIsRefusedBeforeTheSignatureCheckAndNothingIsInstalled()
    {
        using var server = new LoopbackFileServer(new Dictionary<string, byte[]> { ["IntuneWinAppUtil.exe"] = [(byte)'M', (byte)'Z', 1, 2, 3, 4, 5, 6] });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new IntuneWinToolService().InstallAsync(_folder, server.Url("IntuneWinAppUtil.exe")));

        Assert.Contains("SHA-256", ex.Message);
        Assert.Contains(IntuneWinToolService.PinnedSha256, ex.Message);
        Assert.False(File.Exists(Path.Combine(_folder, "IntuneWinAppUtil.exe")));
        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Fact]
    public async Task AFileWithTheRightHashStillHasToCarryAMicrosoftSignature()
    {
        var bytes = new byte[] { (byte)'M', (byte)'Z', 9, 8, 7, 6, 5, 4 };
        using var server = new LoopbackFileServer(new Dictionary<string, byte[]> { ["IntuneWinAppUtil.exe"] = bytes });
        var hash = Convert.ToHexString(SHA256.HashData(bytes));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new IntuneWinToolService().InstallAsync(_folder, server.Url("IntuneWinAppUtil.exe"), expectedSha256: hash));

        Assert.DoesNotContain("SHA-256", ex.Message);
        Assert.Contains("signature", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(_folder, "IntuneWinAppUtil.exe")));
    }
}
