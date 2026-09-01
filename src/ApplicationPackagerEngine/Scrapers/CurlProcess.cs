using System.Diagnostics;

namespace ApplicationPackagerEngine.Scrapers;

/// <summary>
/// Starts curl.exe with the GitHub API Authorization header attached when the
/// target URL needs one. Every HTTP path in the engine goes through here so a
/// token applies to scrapes and downloads alike.
/// </summary>
public static class CurlProcess
{
    /// <summary>
    /// Starts curl with the given arguments. When the URL is a GitHub API call and a
    /// token is available, an extra config is piped over stdin; stdin stays closed
    /// otherwise so curl never waits on it.
    /// </summary>
    public static Process Start(string arguments, string url)
    {
        var authConfig = GitHubAuth.CurlConfigFor(url);

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "curl.exe",
                Arguments = authConfig is null ? arguments : arguments + " -K -",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = authConfig is not null,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();

        if (authConfig is not null)
        {
            process.StandardInput.Write(authConfig);
            process.StandardInput.Close();
        }

        return process;
    }
}
