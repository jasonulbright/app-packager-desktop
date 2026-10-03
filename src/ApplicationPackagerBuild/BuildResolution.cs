using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Models.Workbench;
using ApplicationPackagerEngine.Services.Workbench;

namespace ApplicationPackagerBuild;

/// <summary>A run that cannot start or continue; the message is the whole answer to the operator.</summary>
public sealed class BuildRefusal(string message) : Exception(message);

/// <summary>
/// The application a value names: an id that already carries its prefix is taken as
/// given; a bare value (an id, a definition file name or a packager file name, with or
/// without its extension) is a catalog application when the catalog has it, else a
/// custom one when the user-authored definitions have it, else the catalog id, which
/// then fails as not found.
/// </summary>
public static partial class ApplicationResolver
{
    [GeneratedRegex("^(catalog|custom|byo):", RegexOptions.IgnoreCase)]
    private static partial Regex PrefixPattern();

    public static string Resolve(string value, IReadOnlyList<WorkbenchApplication> discovered)
    {
        var text = value.Trim();
        if (PrefixPattern().IsMatch(text))
        {
            var colon = text.IndexOf(':');
            var id = text[..colon].ToLowerInvariant() + text[colon..];
            if (!ApplicationIds.IsValid(id))
                throw new BuildRefusal($"Invalid ApplicationId '{text}'; expected catalog:<name>, custom:<name> or byo:<id>.");
            return id;
        }

        var baseName = Path.GetFileNameWithoutExtension(text);
        if (string.IsNullOrWhiteSpace(baseName)) throw new BuildRefusal($"Invalid application '{value}'.");
        foreach (var origin in new[] { ApplicationOrigin.Catalog, ApplicationOrigin.Custom })
        {
            var match = discovered.FirstOrDefault(a => a.Origin == origin && NameOf(a.ApplicationId).Equals(baseName, StringComparison.OrdinalIgnoreCase))
                        ?? discovered.FirstOrDefault(a => a.Origin == origin && a.SourcePath is not null
                                                          && Path.GetFileNameWithoutExtension(a.SourcePath).Equals(baseName, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match.ApplicationId;
        }

        var fallback = $"{ApplicationIds.CatalogPrefix}:{baseName}";
        if (!ApplicationIds.IsValid(fallback))
            throw new BuildRefusal($"Invalid ApplicationId '{fallback}'; expected catalog:<name>, custom:<name> or byo:<id>.");
        return fallback;
    }

    /// <summary>The discovered catalog or custom application with this id; null when no source has it.</summary>
    public static WorkbenchApplication? Source(string applicationId, IReadOnlyList<WorkbenchApplication> discovered) =>
        discovered.FirstOrDefault(a => a.Origin != ApplicationOrigin.Byo
                                       && string.Equals(a.ApplicationId, applicationId, StringComparison.OrdinalIgnoreCase));

    private static string NameOf(string applicationId) => applicationId[(applicationId.IndexOf(':') + 1)..];
}

/// <summary>The profile a value names: the default profile, else a profile id, else exactly one profile name.</summary>
public static class ProfileResolver
{
    public static string Resolve(WorkbenchStore store, string applicationId, string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || ApplicationIds.IsDefaultProfile(value.Trim())) return ApplicationIds.DefaultProfileId;
        var wanted = value.Trim();
        var profiles = store.GetProfiles(applicationId);
        var match = profiles.Where(p => string.Equals(p.ProfileId, wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        if (match.Count == 0) match = profiles.Where(p => string.Equals(p.Name, wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        if (match.Count == 0) throw new BuildRefusal($"Profile '{wanted}' was not found for application '{applicationId}'.");
        if (match.Count > 1) throw new BuildRefusal($"Profile name '{wanted}' is ambiguous for application '{applicationId}'; pass the profile id.");
        return match[0].ProfileId;
    }
}

/// <summary>
/// The WSUS settings of one run: each --wsus-* value outranks the stored one. A stored
/// port belongs to the stored SSL choice, so an SSL change without --wsus-port takes the
/// default port of the new scheme. The publisher validates every value again before it connects.
/// </summary>
public static class WsusSettingsResolver
{
    public static WsusSettings Resolve(WsusSettings? stored, BuildOptions options)
    {
        stored ??= new WsusSettings();
        var settings = new WsusSettings
        {
            ServerName = options.WsusServer ?? stored.ServerName,
            UseSsl = options.WsusUseSsl ?? stored.UseSsl,
            Classification = options.WsusClassification ?? (string.IsNullOrWhiteSpace(stored.Classification) ? WsusClassifications.Updates : stored.Classification),
            ApprovalGroup = options.WsusApprovalGroup ?? stored.ApprovalGroup,
            DeclineSuperseded = options.WsusDeclineSuperseded ?? stored.DeclineSuperseded
        };
        settings.PortNumber = options.WsusPort
                              ?? (stored.PortNumber is >= 1 and <= 65535 && stored.UseSsl == settings.UseSsl
                                  ? stored.PortNumber
                                  : settings.UseSsl ? WsusSettings.SslPort : WsusSettings.HttpPort);
        return settings;
    }
}
