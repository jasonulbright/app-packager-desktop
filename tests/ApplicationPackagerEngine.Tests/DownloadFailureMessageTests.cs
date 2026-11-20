using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>A download whose host name does not resolve names the vendor host as the cause.</summary>
public sealed class DownloadFailureMessageTests
{
    [Fact]
    public void A_host_that_does_not_resolve_gets_the_vendor_host_text()
    {
        var text = DownloadService.FailureMessage(2, DownloadService.CurlCouldNotResolveHost, "https://jabra.example.net/jdo/JabraDirectSetup.exe");

        Assert.Equal(
            "Download failed: the vendor download host does not resolve (jabra.example.net). The vendor site can be down, or the vendor removed the host. Try again later. Download address: https://jabra.example.net/jdo/JabraDirectSetup.exe",
            text);
    }

    [Fact]
    public void Any_other_curl_failure_keeps_the_general_text()
    {
        var text = DownloadService.FailureMessage(2, 22, "https://vendor.example.net/setup.exe");

        Assert.Equal("Download failed after 2 attempts: https://vendor.example.net/setup.exe", text);
    }
}
