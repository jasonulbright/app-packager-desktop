using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ApplicationPackagerEngine.Services.VendorImport;

/// <summary>How the program authenticates to a feed (methods 1 and 2 of "Authenticated feeds"; the others come later).</summary>
public enum FeedAuthMethod { HeaderKey, Basic }

/// <summary>
/// A credential entry: the id of the secret in the store, the bound host and port (HTTPS only), the method and the non-secret values. It holds
/// no secret. The settings file holds these entries and nothing else.
/// </summary>
public sealed record FeedCredentialEntry(string Id, string Host, int Port, FeedAuthMethod Method, string? HeaderName, string? UserName)
{
    /// <summary>The bound scheme, host and port: the credential goes to a request with exactly these.</summary>
    public bool Matches(Uri address) =>
        address.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && address.Host.Equals(Host, StringComparison.OrdinalIgnoreCase) && address.Port == Port;

    public string HostText => Port == 443 ? Host : $"{Host}:{Port}";
}

/// <summary>An entry with its secret, as the fetch needs it. It lives in memory for one fetch.</summary>
public sealed record FeedCredential(FeedCredentialEntry Entry, string Secret)
{
    /// <summary>The header that carries the credential to the bound host: its name and its value.</summary>
    public (string Name, string Value) Header()
    {
        if (Entry.Method == FeedAuthMethod.Basic)
            return ("Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes((Entry.UserName ?? "") + ":" + Secret)));
        var name = string.IsNullOrWhiteSpace(Entry.HeaderName) ? "Authorization" : Entry.HeaderName!;
        // Authorization carries a bearer key; another header (X-License-Key) carries the key as it is.
        return name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ? (name, "Bearer " + Secret) : (name, Secret);
    }
}

/// <summary>Where the secrets live. The Windows Credential Manager in the program; a fake in a test.</summary>
public interface ISecretStore
{
    string? Read(string id);
    void Write(string id, string secret);
    void Delete(string id);
}

/// <summary>
/// The Windows Credential Manager, as generic credentials of the current Windows user, persisted on the local computer only (not the enterprise
/// persistence, which roams). A secret over the limit of the Credential Manager is refused with a message.
/// </summary>
public sealed class WindowsCredentialStore : ISecretStore
{
    public const int MaxSecretBytes = 2560;
    private const string Prefix = "AppPackagerDesktop/Feed/";
    private const int TypeGeneric = 1;
    private const int PersistLocalMachine = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref Credential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, uint reservedFlag, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    public string? Read(string id)
    {
        if (!CredRead(Prefix + id, TypeGeneric, 0, out var pointer)) return null;
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            if (credential.CredentialBlobSize == 0) return "";
            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.Unicode.GetString(bytes);
        }
        finally { CredFree(pointer); }
    }

    public void Write(string id, string secret)
    {
        var bytes = Encoding.Unicode.GetBytes(secret);
        if (bytes.Length > MaxSecretBytes)
            throw new InvalidOperationException($"The secret is larger than {MaxSecretBytes} bytes, the limit of the Windows Credential Manager.");
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new Credential
            {
                Type = TypeGeneric, TargetName = Prefix + id, CredentialBlobSize = (uint)bytes.Length, CredentialBlob = blob,
                Persist = PersistLocalMachine, UserName = "AppPackagerDesktop"
            };
            if (!CredWrite(ref credential, 0))
                throw new InvalidOperationException("The Windows Credential Manager refused the secret (error " + Marshal.GetLastWin32Error() + ").");
        }
        finally
        {
            Array.Clear(bytes);
            Marshal.Copy(new byte[bytes.Length], 0, blob, bytes.Length);
            Marshal.FreeHGlobal(blob);
        }
    }

    public void Delete(string id) => _ = CredDelete(Prefix + id, TypeGeneric, 0);
}

/// <summary>
/// The credential entries of the feeds. The settings file holds the id, the bound host, the method and the non-secret values; the secret goes to the
/// store. Rotate overwrites the secret; Remove deletes the entry and the secret.
/// </summary>
public sealed class FeedCredentialStore(string settingsPath, ISecretStore secrets)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string DefaultPath => Path.Combine(AppDataPaths.Root, "FeedCredentials.json");

    public static FeedCredentialStore Default() => new(DefaultPath, new WindowsCredentialStore());

    /// <summary>The entries for a fetch and for the list: an unreadable file gives none (fail closed, no credential is sent).</summary>
    public IReadOnlyList<FeedCredentialEntry> Entries()
    {
        try { return EntriesStrict(); }
        catch (InvalidOperationException) { return []; }
    }

    /// <summary>The entries for a change: an unreadable file stops the change, so that it never overwrites the entries that it cannot read.</summary>
    private List<FeedCredentialEntry> EntriesStrict()
    {
        if (!File.Exists(settingsPath)) return [];
        try { return JsonSerializer.Deserialize<List<FeedCredentialEntry>>(File.ReadAllText(settingsPath)) ?? []; }
        catch (JsonException ex) { throw new InvalidOperationException($"The file of the feed credentials ({Path.GetFileName(settingsPath)}) cannot be read ({ex.Message}). Nothing was changed; repair or delete the file.", ex); }
    }

    public const int MinSecretLength = 8;

    private void Save(IEnumerable<FeedCredentialEntry> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        var temp = settingsPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(entries.ToList(), Json));
        File.Move(temp, settingsPath, overwrite: true);
    }

    private static readonly Regex HeaderName = new(@"^[A-Za-z0-9-]{1,64}\z", RegexOptions.Compiled);
    private static readonly Regex IdGrammar = new(@"^[a-z0-9-]{3,64}\z", RegexOptions.Compiled);

    /// <summary>Adds an entry bound to the host of <paramref name="address"/>. The secret goes to the store; the file holds no secret.</summary>
    public FeedCredentialEntry Add(string id, Uri address, FeedAuthMethod method, string? headerName, string? userName, string secret)
    {
        FeedFetcher.CheckAddress(address, "The address of the feed");
        if (!IdGrammar.IsMatch(id)) throw new InvalidOperationException("The id of the entry is 3 to 64 characters of a-z, 0-9 and '-'.");
        if (secret.Length < MinSecretLength) throw new InvalidOperationException($"The secret is shorter than {MinSecretLength} characters; a short value cannot be kept out of the messages.");
        if (method == FeedAuthMethod.HeaderKey && !string.IsNullOrWhiteSpace(headerName) && !HeaderName.IsMatch(headerName))
            throw new InvalidOperationException("The header name has a character that a header name cannot have.");
        if (method == FeedAuthMethod.Basic && string.IsNullOrWhiteSpace(userName)) throw new InvalidOperationException("Basic needs a user name.");
        if (method == FeedAuthMethod.Basic && (userName!.Contains(':') || userName.Any(char.IsControl))) throw new InvalidOperationException("A Basic user name has no ':' and no control character (RFC 7617).");
        var entries = EntriesStrict();
        if (entries.Any(e => e.Id == id)) throw new InvalidOperationException($"An entry with the id '{id}' exists. Rotate its secret, or remove it first.");
        if (entries.FirstOrDefault(e => e.Host.Equals(address.Host, StringComparison.OrdinalIgnoreCase) && e.Port == address.Port) is { } same)
            throw new InvalidOperationException($"The host {same.HostText} has the entry '{same.Id}' already. Rotate its secret, or remove it first.");
        var entry = new FeedCredentialEntry(id, address.Host.ToLowerInvariant(), address.Port, method,
            method == FeedAuthMethod.HeaderKey ? (string.IsNullOrWhiteSpace(headerName) ? "Authorization" : headerName!.Trim()) : null,
            method == FeedAuthMethod.Basic ? userName!.Trim() : null);
        // The secret first: a failure of the store leaves no entry that points to nothing.
        secrets.Write(id, secret);
        SecretRedactor.Register(secret);
        Save([.. entries, entry]);
        return entry;
    }

    public void Rotate(string id, string secret)
    {
        if (EntriesStrict().All(e => e.Id != id)) throw new InvalidOperationException($"There is no entry '{id}'.");
        if (secret.Length < MinSecretLength) throw new InvalidOperationException($"The secret is shorter than {MinSecretLength} characters; a short value cannot be kept out of the messages.");
        secrets.Write(id, secret);
        SecretRedactor.Register(secret);
    }

    public void Remove(string id)
    {
        Save(EntriesStrict().Where(e => e.Id != id));
        secrets.Delete(id);
    }

    /// <summary>The credential for a request to <paramref name="address"/>: the entry that is bound to exactly its scheme, host and port, with its secret; null when none.</summary>
    public FeedCredential? For(Uri address)
    {
        var entry = Entries().FirstOrDefault(e => e.Matches(address));
        if (entry is null) return null;
        var secret = secrets.Read(entry.Id);
        if (secret is null) throw new FeedFetchException($"The entry '{entry.Id}' for {entry.HostText} has no secret in the Windows Credential Manager. Rotate it to enter the secret again.");
        SecretRedactor.Register(secret);
        return new FeedCredential(entry, secret);
    }
}

/// <summary>
/// The redactor of the outputs that the program writes (log lines, error messages, reports, records, exports). It removes any secret value that was
/// registered, the values of the headers that carry a secret, and the secret parts of an address.
/// </summary>
public static partial class SecretRedactor
{
    private static readonly HashSet<string> Secrets = new(StringComparer.Ordinal);
    private static readonly object Gate = new();

    public const string Mask = "[redacted]";

    /// <summary>A secret value that must never appear in an output. A value shorter than 4 characters is not registered (it would redact ordinary text).</summary>
    public static void Register(string secret)
    {
        if (secret.Length < 4) return;
        lock (Gate) Secrets.Add(secret);
    }

    [GeneratedRegex(@"(?im)\b(authorization|proxy-authorization|x-[a-z0-9-]*(?:key|token|secret|auth)[a-z0-9-]*|api-?key|cookie|set-cookie)\s*[:=]\s*(?:(?:bearer|basic|negotiate|digest)\s+)?[^\s""']+")]
    private static partial Regex SecretHeader();

    [GeneratedRegex(@"(?i)([?&](?:token|access_token|id_token|auth|authorization|key|apikey|api_key|api-key|password|passwd|pwd|secret|client_secret|sig|signature|sas|x-amz-signature|x-amz-credential)=)[^&\s""']*")]
    private static partial Regex SecretQuery();

    [GeneratedRegex(@"(?i)\bhttps?://[^\s/@:]+:[^\s/@]+@")]
    private static partial Regex UserInfo();

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var result = SecretHeader().Replace(text, m => m.Value[..(m.Value.IndexOfAny([':', '=']) + 1)] + " " + Mask);
        result = SecretQuery().Replace(result, "$1" + Mask);
        result = UserInfo().Replace(result, m => m.Value[..m.Value.IndexOf("://", StringComparison.Ordinal)] + "://" + Mask + "@");
        string[] known;
        lock (Gate) known = [.. Secrets];
        foreach (var secret in known.OrderByDescending(s => s.Length))
            result = result.Replace(secret, Mask, StringComparison.Ordinal);
        return result;
    }
}
