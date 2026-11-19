using ApplicationPackagerEngine.Services.VendorImport;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// For a vendor that serves one file under two addresses (Tableau: esdalt and tssoftware). The addresses are tried in order. An address
/// is skipped for any reason: a refused or missing file (403, 404, another HTTP error), a connection failure, a body that is no installer
/// (a sign-in page), or a failed check of the download. The log says which address was used and why each earlier one was skipped. The
/// stage fails only when every address failed, and the message lists each address with its reason.
/// </summary>
public static class DownloadAddresses
{
    /// <summary>
    /// Runs <paramref name="download"/> for each address until one succeeds; returns the address that was used. With one address the
    /// download runs once and its own exception passes through unchanged. A cancellation is never a reason to try the next address.
    /// </summary>
    public static async Task<string> TryInOrderAsync(IReadOnlyList<string> addresses, Func<string, Task> download, Action<string>? onProgress = null)
    {
        var list = addresses.Where(a => !string.IsNullOrWhiteSpace(a)).ToList();
        if (list.Count == 0) throw new InvalidOperationException("The definition has no download address.");
        if (list.Count == 1)
        {
            await download(list[0]);
            return list[0];
        }

        var failures = new List<string>();
        for (var i = 0; i < list.Count; i++)
        {
            try
            {
                await download(list[i]);
                onProgress?.Invoke($"Download address used: {SecretRedactor.WithoutQueries(list[i])}");
                return list[i];
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var reason = SecretRedactor.Redact(SecretRedactor.WithoutQueries(ex.Message));
                failures.Add($"{SecretRedactor.WithoutQueries(list[i])}: {reason}");
                if (i < list.Count - 1)
                    onProgress?.Invoke($"Address skipped: {failures[^1]}. Trying the next one.");
            }
        }
        throw new InvalidOperationException("The installer could not be downloaded from any of its addresses. " + string.Join(" | ", failures));
    }
}
