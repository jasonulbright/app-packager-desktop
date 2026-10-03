using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ApplicationPackagerEngine.Models.Signing;

/// <summary>
/// Script-signing policy. Three categories (Detection, Requirements,
/// Deployment) each carry an independent Sign switch and Require switch:
/// Sign alone is best effort, and Require is the only switch that refuses a
/// publish. Every switch defaults off.
/// </summary>
public sealed class SigningPolicy
{
    public const string CurrentUser = "CurrentUser";
    public const string LocalMachine = "LocalMachine";

    public bool SignDetection { get; set; }
    public bool SignRequirements { get; set; }
    public bool SignDeployment { get; set; }
    public bool RequireDetection { get; set; }
    public bool RequireRequirements { get; set; }
    public bool RequireDeployment { get; set; }
    public string CertificateThumbprint { get; set; } = "";
    public string StoreLocation { get; set; } = CurrentUser;
    public string TimestampServer { get; set; } = "";
    public bool TimestampRequired { get; set; }
    public string HashAlgorithm { get; set; } = "SHA256";

    /// <summary>
    /// SHA-256 (lowercase hex) over the canonical "Key=Value;..." join of the
    /// normalized policy in the fixed key order. Booleans render as True and
    /// False, so the digest equals the one a PowerShell host computes for the
    /// same policy.
    /// </summary>
    [JsonIgnore]
    public string PolicyDigest => ComputeDigest(Normalize(this));

    [JsonIgnore]
    public bool AnySign => SignDetection || SignRequirements || SignDeployment;

    [JsonIgnore]
    public bool AnySwitch => AnySign || RequireDetection || RequireRequirements || RequireDeployment;

    /// <summary>
    /// The policy a settings file stores, made safe to adopt: a missing policy is
    /// all off, a thumbprint that is not exactly 40 hex characters is dropped rather
    /// than adopted, an unknown store location is CurrentUser, the timestamp URL is
    /// trimmed and a blank hash algorithm is SHA256.
    /// </summary>
    public static SigningPolicy FromSettings(SigningPolicy? stored)
    {
        if (stored is null) return new SigningPolicy();
        var thumbprint = (stored.CertificateThumbprint ?? "").Trim();
        var store = (stored.StoreLocation ?? "") switch
        {
            var s when s.Equals(LocalMachine, StringComparison.OrdinalIgnoreCase) => LocalMachine,
            _ => CurrentUser
        };
        return new SigningPolicy
        {
            SignDetection = stored.SignDetection,
            SignRequirements = stored.SignRequirements,
            SignDeployment = stored.SignDeployment,
            RequireDetection = stored.RequireDetection,
            RequireRequirements = stored.RequireRequirements,
            RequireDeployment = stored.RequireDeployment,
            CertificateThumbprint = Regex.IsMatch(thumbprint, "^[0-9A-Fa-f]{40}$") ? thumbprint.ToUpperInvariant() : "",
            StoreLocation = store,
            TimestampServer = (stored.TimestampServer ?? "").Trim(),
            TimestampRequired = stored.TimestampRequired,
            HashAlgorithm = string.IsNullOrWhiteSpace(stored.HashAlgorithm) ? "SHA256" : stored.HashAlgorithm.Trim().ToUpperInvariant()
        };
    }

    public bool Sign(SigningCategory category) => category switch
    {
        SigningCategory.Detection => SignDetection,
        SigningCategory.Requirements => SignRequirements,
        _ => SignDeployment
    };

    public bool Require(SigningCategory category) => category switch
    {
        SigningCategory.Detection => RequireDetection,
        SigningCategory.Requirements => RequireRequirements,
        _ => RequireDeployment
    };

    /// <summary>The fixed key order the digest and every round trip use.</summary>
    public static readonly IReadOnlyList<string> KeyOrder =
    [
        nameof(SignDetection), nameof(SignRequirements), nameof(SignDeployment),
        nameof(RequireDetection), nameof(RequireRequirements), nameof(RequireDeployment),
        nameof(CertificateThumbprint), nameof(StoreLocation), nameof(TimestampServer),
        nameof(TimestampRequired), nameof(HashAlgorithm)
    ];

    /// <summary>
    /// A normalized copy: StoreLocation is exactly CurrentUser or LocalMachine
    /// (anything else throws), HashAlgorithm is upper-case and defaults to
    /// SHA256, and CertificateThumbprint keeps only hex digits, upper-cased.
    /// A null source yields the all-off defaults.
    /// </summary>
    public static SigningPolicy Normalize(SigningPolicy? source)
    {
        source ??= new SigningPolicy();
        var store = (source.StoreLocation ?? "") switch
        {
            var s when s.Equals(LocalMachine, StringComparison.OrdinalIgnoreCase) => LocalMachine,
            var s when s.Equals(CurrentUser, StringComparison.OrdinalIgnoreCase) => CurrentUser,
            _ => throw new InvalidOperationException(
                $"Unsupported signing StoreLocation '{source.StoreLocation}'. Use CurrentUser or LocalMachine.")
        };
        var hash = string.IsNullOrWhiteSpace(source.HashAlgorithm) ? "SHA256" : source.HashAlgorithm;
        return new SigningPolicy
        {
            SignDetection = source.SignDetection,
            SignRequirements = source.SignRequirements,
            SignDeployment = source.SignDeployment,
            RequireDetection = source.RequireDetection,
            RequireRequirements = source.RequireRequirements,
            RequireDeployment = source.RequireDeployment,
            CertificateThumbprint = Regex.Replace(source.CertificateThumbprint ?? "", "[^0-9A-Fa-f]", "").ToUpperInvariant(),
            StoreLocation = store,
            TimestampServer = source.TimestampServer ?? "",
            TimestampRequired = source.TimestampRequired,
            HashAlgorithm = hash.ToUpperInvariant()
        };
    }

    /// <summary>
    /// Reads a policy from JSON. A missing, null or unparsable switch value
    /// stays off: only true/1/yes/on (any case) enables a switch.
    /// </summary>
    public static SigningPolicy FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Normalize(null);
        return FromJsonNode(JsonNode.Parse(json));
    }

    /// <summary>
    /// One switch of a JSON policy, read the way <see cref="FromJsonNode"/>
    /// reads it, without normalizing the rest of the policy.
    /// </summary>
    public static bool ReadSwitch(JsonNode? node, string key) => ToBoolean(ReadText(node as JsonObject, key));

    private static string? ReadText(JsonObject? obj, string key)
    {
        if (obj is null) return null;
        var match = obj.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));
        if (match.Value is null) return null;
        return match.Value.GetValueKind() switch
        {
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.String => match.Value.GetValue<string>(),
            JsonValueKind.Number => match.Value.ToJsonString(),
            _ => null
        };
    }

    public static SigningPolicy FromJsonNode(JsonNode? node)
    {
        var obj = node as JsonObject;
        string? Read(string key) => ReadText(obj, key);

        return Normalize(new SigningPolicy
        {
            SignDetection = ToBoolean(Read(nameof(SignDetection))),
            SignRequirements = ToBoolean(Read(nameof(SignRequirements))),
            SignDeployment = ToBoolean(Read(nameof(SignDeployment))),
            RequireDetection = ToBoolean(Read(nameof(RequireDetection))),
            RequireRequirements = ToBoolean(Read(nameof(RequireRequirements))),
            RequireDeployment = ToBoolean(Read(nameof(RequireDeployment))),
            CertificateThumbprint = Read(nameof(CertificateThumbprint)) ?? "",
            StoreLocation = Read(nameof(StoreLocation)) ?? CurrentUser,
            TimestampServer = Read(nameof(TimestampServer)) ?? "",
            TimestampRequired = ToBoolean(Read(nameof(TimestampRequired))),
            HashAlgorithm = Read(nameof(HashAlgorithm)) ?? "SHA256"
        });
    }

    /// <summary>true/1/yes/on is true, false/0/no/off is false, anything else is the default.</summary>
    public static bool ToBoolean(string? value, bool defaultValue = false)
    {
        if (string.IsNullOrWhiteSpace(value)) return defaultValue;
        return value.Trim().ToLowerInvariant() switch
        {
            "true" or "1" or "yes" or "on" => true,
            "false" or "0" or "no" or "off" => false,
            _ => defaultValue
        };
    }

    /// <summary>The canonical "Key=Value" join the digest hashes.</summary>
    public string CanonicalString()
    {
        string Render(bool b) => b ? "True" : "False";
        var values = new[]
        {
            Render(SignDetection), Render(SignRequirements), Render(SignDeployment),
            Render(RequireDetection), Render(RequireRequirements), Render(RequireDeployment),
            CertificateThumbprint, StoreLocation, TimestampServer,
            Render(TimestampRequired), HashAlgorithm
        };
        return string.Join(";", KeyOrder.Select((k, i) => k + "=" + values[i]));
    }

    private static string ComputeDigest(SigningPolicy normalized) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized.CanonicalString())))
            .ToLower(CultureInfo.InvariantCulture);
}

/// <summary>The three independent signing categories.</summary>
public enum SigningCategory
{
    Detection,
    Requirements,
    Deployment
}
