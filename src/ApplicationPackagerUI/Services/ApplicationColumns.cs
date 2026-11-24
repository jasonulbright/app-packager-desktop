using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services.Supersedence;
using ApplicationPackagerUI.Models;

namespace ApplicationPackagerUI.Services;

/// <summary>
/// The working copies that the columns of the Application Settings grid edit until Save Settings: the supersedence settings, the saved
/// conditions and command overrides, and the One Click rows. A column reads and writes through this object only.
/// </summary>
public sealed class ApplicationWork
{
    public SupersedenceSettings Supersedence { get; set; } = new();
    public Dictionary<string, DeploymentConditionEntry> Conditions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, CommandOverrideEntry> Commands { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> ConditionsTouched { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> CommandsTouched { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ApplicationDistribution> Distribution { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> DistributionTouched { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The One Click row of an application (the work copy of the One Click grid); null when the application has none.</summary>
    public Func<string, OneClickRow?> OneClickRowOf { get; set; } = _ => null;

    /// <summary>The Workbench profile fields of an application as stored (runtime, reboot behavior) and whether another profile is active.</summary>
    public Func<SupersedenceRow, GridProfileFields.State> ProfileOf { get; set; } = _ => GridProfileFields.State.Empty;

    /// <summary>The profile field values that Save Settings writes, by application identifier then field path (an empty value or Inherit clears the field).</summary>
    public Dictionary<string, Dictionary<string, string>> ProfilePending { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The definition identifier of each application in <see cref="ProfilePending"/>.</summary>
    public Dictionary<string, string> ProfileDefinition { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The value of a profile field: the pending edit, else the stored one.</summary>
    public string ProfileValue(SupersedenceRow row, string path) =>
        ProfilePending.TryGetValue(row.ApplicationId, out var pending) && pending.TryGetValue(path, out var value) ? value : ProfileOf(row).Fields.GetValueOrDefault(path, "");

    public bool SetProfileValue(SupersedenceRow row, string path, string value)
    {
        if (!GridProfileFields.IsValid(path, value)) return false;
        if (!ProfilePending.TryGetValue(row.ApplicationId, out var pending)) ProfilePending[row.ApplicationId] = pending = new(StringComparer.Ordinal);
        pending[path] = value;
        ProfileDefinition[row.ApplicationId] = row.DefinitionId;
        return true;
    }

    /// <summary>The reason a profile field cannot be set here: another profile is active for the application.</summary>
    public string? ProfileReason(SupersedenceRow row) => ProfileOf(row).ActiveElsewhere is { } name
        ? $"This application uses its Workbench profile {name}. Set this value in the Workbench, or make the Migrated profile the active one." : null;

    /// <summary>The One Click default destinations, which an application follows until it has its own.</summary>
    public Func<DestinationSet> DefaultDestinations { get; set; } = () => new(true, false, false);
}

/// <summary>
/// One column of the Application Settings grid. A column is one registration: its header, its choices, how it reads and writes the
/// stored value of an application, and when it does not apply. The grid, the column chooser and the bulk bar are built from the list
/// <see cref="ApplicationColumns.All"/>; a new per-application setting is one more entry there and no new screen.
/// </summary>
public sealed class ApplicationColumn
{
    public required string Id { get; init; }
    public required string Header { get; init; }

    /// <summary>The name that a screen reader announces for the cell, and that the lab scripts find it by.</summary>
    public required string AutomationName { get; init; }

    public required string Tooltip { get; init; }
    public required int Width { get; init; }

    /// <summary>The choices of the cell; null for a text cell.</summary>
    public IReadOnlyList<string>? Choices { get; init; }

    /// <summary>The value that returns the application to its default (the first choice for a column with choices, the empty text for a text column).</summary>
    public string ResetValue => Choices is { Count: > 0 } ? Choices[0] : "";

    /// <summary>True when the column is in the grid before the person picks columns.</summary>
    public bool DefaultVisible { get; init; }

    public required Func<ApplicationWork, SupersedenceRow, string> Get { get; init; }

    /// <summary>Writes a value; false when the value is not one of the choices (nothing changes).</summary>
    public required Func<ApplicationWork, SupersedenceRow, string, bool> Set { get; init; }

    /// <summary>The reason the setting does not apply to the application; null when it applies. The cell is then disabled and its tooltip says why.</summary>
    public Func<ApplicationWork, SupersedenceRow, string?> NotApplicable { get; init; } = (_, _) => null;
}

/// <summary>The registration of every per-application setting that the grid shows.</summary>
public static class ApplicationColumns
{
    private const string YesNoDefault = "Follow the default";
    private static readonly IReadOnlyList<string> YesNo = [YesNoDefault, "Yes", "No"];

    public static readonly IReadOnlyList<ApplicationColumn> All = Build();

    public static ApplicationColumn? Find(string? id) => All.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.Ordinal));

    private static ApplicationColumn DestinationColumn(string id, string header, string system, Func<DestinationSet, bool> get, Action<DestinationSet, bool> set,
        Func<SupersedenceRow, string?> notApplicable) => new()
    {
        Id = id, Header = header, AutomationName = $"{header} of the application", Width = 150, Choices = YesNo,
        Tooltip = $"Yes publishes the application to {system} in One Click and sets it as its own choice. No leaves {system} out. Follow the default uses the One Click default destinations.",
        Get = (w, r) => w.OneClickRowOf(r.Uid) is { } oc ? (IsOwn(w, oc) ? (get(oc.Destinations) ? "Yes" : "No") : YesNoDefault) : YesNoDefault,
        Set = (w, r, v) =>
        {
            if (!YesNo.Contains(v) || w.OneClickRowOf(r.Uid) is not { } oc) return false;
            var current = oc.Destinations;
            if (v == YesNoDefault)
            {
                var d = w.DefaultDestinations();
                set(current, get(d));
            }
            else set(current, v == "Yes");
            oc.ConfigMgr = current.ConfigMgr; oc.Wsus = current.Wsus; oc.Intune = current.Intune;
            return true;
        },
        NotApplicable = (w, r) => w.OneClickRowOf(r.Uid) is null ? "One Click does not list this application." : notApplicable(r)
    };

    private static bool SetDistribution(ApplicationWork w, SupersedenceRow r, string? dp, string? test)
    {
        if (!w.Distribution.TryGetValue(r.ApplicationId, out var entry)) w.Distribution[r.ApplicationId] = entry = new ApplicationDistribution();
        if (dp is not null) entry.DpGroupName = dp;
        if (test is not null) entry.TestCollectionName = test;
        if (string.IsNullOrWhiteSpace(entry.DpGroupName) && string.IsNullOrWhiteSpace(entry.TestCollectionName)) w.Distribution.Remove(r.ApplicationId);
        w.DistributionTouched.Add(r.ApplicationId);
        return true;
    }

    private static bool Touch(ApplicationWork w, SupersedenceRow r) { w.ConditionsTouched.Add(r.DefinitionId); return true; }

    /// <summary>True when the application publishes to other destinations than the default.</summary>
    private static bool IsOwn(ApplicationWork w, OneClickRow oc) => !oc.Destinations.Equals(w.DefaultDestinations());

    private static IReadOnlyList<ApplicationColumn> Build() =>
    [
        new ApplicationColumn
        {
            Id = "source", Header = "Architecture", AutomationName = "Architecture of the application", Width = 150, DefaultVisible = true,
            Choices = ApplicationGridSettings.ArchitectureChoices,
            Tooltip = "Select the architecture that the package targets. It is offered only for an application that has an architecture choice; the others show the fixed architecture.",
            Get = (w, r) => r.OffersArchitecture ? ApplicationGridSettings.ArchitectureOf(ApplicationGridSettings.Find(w.Conditions, r.DefinitionId)) : r.ArchitectureText,
            Set = (w, r, v) => r.OffersArchitecture && ApplicationGridSettings.SetArchitecture(w.Conditions, r.DefinitionId, v) && Touch(w, r),
            NotApplicable = (_, r) => r.OffersArchitecture ? null : $"This application has one architecture ({r.Architecture}), so there is no choice."
        },
        new ApplicationColumn
        {
            Id = "scope", Header = "Install scope", AutomationName = "Install scope of the application", Width = 165, DefaultVisible = true,
            Choices = ApplicationGridSettings.ScopeChoices,
            Tooltip = "Select System to install for all users or User to install for the signed-in user. It is offered only for an installer that supports both.",
            Get = (w, r) => r.OffersInstallScope ? ApplicationGridSettings.ScopeOf(ApplicationGridSettings.Find(w.Conditions, r.DefinitionId)) : "Per installer",
            Set = (w, r, v) => r.OffersInstallScope && ApplicationGridSettings.SetScope(w.Conditions, r.DefinitionId, v) && Touch(w, r),
            NotApplicable = (_, r) => r.OffersInstallScope ? null : "The installer of this application supports one install scope only."
        },
        new ApplicationColumn
        {
            Id = "installertype", Header = "Installer type", AutomationName = "Installer type of the application", Width = 150,
            Choices = ApplicationGridSettings.InstallerTypeChoices,
            Tooltip = "Select EXE or MSI. The application stages and installs with that installer, its own silent switches and its own detection. Packager default uses the type that the definition ships. It is offered only for an application that supports both types.",
            Get = (w, r) => r.InstallerTypes.Count >= 2 ? ApplicationGridSettings.InstallerTypeOf(ApplicationGridSettings.Find(w.Conditions, r.DefinitionId)) : ApplicationGridSettings.PackagerDefault,
            Set = (w, r, v) => r.InstallerTypes.Count >= 2 && (v == ApplicationGridSettings.PackagerDefault || r.InstallerTypes.Contains(v))
                && ApplicationGridSettings.SetInstallerType(w.Conditions, r.DefinitionId, v) && Touch(w, r),
            NotApplicable = (_, r) => r.InstallerTypes.Count >= 2 ? null : "The vendor of this application ships one installer type, so there is no choice."
        },
        new ApplicationColumn
        {
            Id = "supersedence", Header = "Supersedence", AutomationName = "Supersedence of the application", Width = 190, DefaultVisible = true,
            Choices = SupersedenceGrid.Choices,
            Tooltip = "Supersedence mode of this application. Follow the default uses the default mode above. Replace and uninstall needs the version rule Exact.",
            Get = (w, r) => SupersedenceGrid.ChoiceOf(w.Supersedence, r.ApplicationId),
            Set = (w, r, v) => SupersedenceGrid.SetChoice(w.Supersedence, r.ApplicationId, v)
        },
        new ApplicationColumn
        {
            Id = "title", Header = "Version in title", AutomationName = "Version in the title of the application", Width = 190, DefaultVisible = true,
            Choices = ApplicationGridSettings.TitleChoices,
            Tooltip = "Include version adds the version to the Application name. No version removes it, so one Application carries every release. It is locked to Include version while supersedence is on.",
            Get = (w, r) => SupersedenceSettingsEditor.LocksVersionInTitle(w.Supersedence, r.ApplicationId)
                ? ApplicationGridSettings.IncludeVersion
                : ApplicationGridSettings.TitleOf(ApplicationGridSettings.Find(w.Conditions, r.DefinitionId)),
            Set = (w, r, v) => !SupersedenceSettingsEditor.LocksVersionInTitle(w.Supersedence, r.ApplicationId)
                && ApplicationGridSettings.SetTitle(w.Conditions, r.DefinitionId, v) && Touch(w, r),
            NotApplicable = (w, r) => SupersedenceSettingsEditor.LocksVersionInTitle(w.Supersedence, r.ApplicationId)
                ? "Supersedence is on for this application, so the version is in the title. Turn supersedence off to change it." : null
        },
        new ApplicationColumn
        {
            Id = "rule", Header = "Version rule", AutomationName = "Version rule of the application", Width = 195, DefaultVisible = true,
            Choices = SupersedenceGrid.RuleChoices,
            Tooltip = "Minimum is this version or newer. Exact is this version only (the detection operator equals). Follow the default uses the version rule above.",
            Get = (w, r) => SupersedenceGrid.RuleChoiceOf(w.Supersedence, r.ApplicationId),
            Set = (w, r, v) => SupersedenceGrid.SetRuleChoice(w.Supersedence, r.ApplicationId, v)
        },
        new ApplicationColumn
        {
            Id = "network", Header = "Network", AutomationName = "Network requirement of the application", Width = 160,
            Choices = ["Any network", "VPN only", "On site only"],
            Tooltip = "Adds a requirement to the deployment type: the computer must be on the VPN, or must not be on the VPN. Any network adds no requirement.",
            Get = (w, r) => ApplicationGridSettings.Find(w.Conditions, r.DefinitionId)?.Network switch
            {
                DeploymentConditionValues.VpnOnly => "VPN only", DeploymentConditionValues.OnSiteOnly => "On site only", _ => "Any network"
            },
            Set = (w, r, v) =>
            {
                var token = v switch { "VPN only" => DeploymentConditionValues.VpnOnly, "On site only" => DeploymentConditionValues.OnSiteOnly, "Any network" => DeploymentConditionValues.Any, _ => null };
                if (token is null) return false;
                ApplicationGridSettings.SetNetwork(w.Conditions, r.DefinitionId, token);
                w.ConditionsTouched.Add(r.DefinitionId);
                return true;
            }
        },
        new ApplicationColumn
        {
            Id = "split", Header = "Network split", AutomationName = "Variant split of the application", Width = 160,
            Choices = ["No split", "VPN and managed"],
            Tooltip = "Makes one deployment type for computers on the VPN (online content) and one for the others. It is offered only for an application that supports the Network split.",
            Get = (w, r) => string.Equals(ApplicationGridSettings.Find(w.Conditions, r.DefinitionId)?.Split, "Network", StringComparison.OrdinalIgnoreCase) ? "VPN and managed" : "No split",
            Set = (w, r, v) =>
            {
                if (!r.OffersNetworkSplit || (v != "No split" && v != "VPN and managed")) return false;
                ApplicationGridSettings.SetNetworkSplit(w.Conditions, r.DefinitionId, v == "VPN and managed");
                w.ConditionsTouched.Add(r.DefinitionId);
                return true;
            },
            NotApplicable = (_, r) => r.OffersNetworkSplit ? null : "This application does not support the Network split."
        },
        new ApplicationColumn
        {
            Id = "install", Header = "Install command", AutomationName = "Install command of the application", Width = 260,
            Tooltip = "Replaces the install command line of the deployment type. Leave it empty for the command of the definition.",
            Get = (w, r) => w.Commands.TryGetValue(r.DefinitionId, out var e) ? e.Install ?? "" : "",
            Set = (w, r, v) => { ApplicationGridSettings.SetCommand(w.Commands, r.DefinitionId, install: v.Trim(), uninstall: null); w.CommandsTouched.Add(r.DefinitionId); return true; }
        },
        new ApplicationColumn
        {
            Id = "uninstall", Header = "Uninstall command", AutomationName = "Uninstall command of the application", Width = 260,
            Tooltip = "Replaces the uninstall command line of the deployment type. Leave it empty for the command of the definition.",
            Get = (w, r) => w.Commands.TryGetValue(r.DefinitionId, out var e) ? e.Uninstall ?? "" : "",
            Set = (w, r, v) => { ApplicationGridSettings.SetCommand(w.Commands, r.DefinitionId, install: null, uninstall: v.Trim()); w.CommandsTouched.Add(r.DefinitionId); return true; }
        },
        new ApplicationColumn
        {
            Id = "estimated", Header = "Estimated runtime (min)", AutomationName = "Estimated runtime of the application", Width = 190,
            Tooltip = "The estimated installation time in minutes that the deployment type shows. Leave it empty to inherit the default runtime. It is stored in the Workbench profile of the application.",
            Get = (w, r) => w.ProfileValue(r, GridProfileFields.EstimatedMinutes),
            Set = (w, r, v) => w.SetProfileValue(r, GridProfileFields.EstimatedMinutes, v.Trim()),
            NotApplicable = (w, r) => w.ProfileReason(r)
        },
        new ApplicationColumn
        {
            Id = "maximum", Header = "Maximum runtime (min)", AutomationName = "Maximum runtime of the application", Width = 190,
            Tooltip = "The maximum allowed run time in minutes of the deployment type. Leave it empty to inherit the default runtime. It is stored in the Workbench profile of the application.",
            Get = (w, r) => w.ProfileValue(r, GridProfileFields.MaximumMinutes),
            Set = (w, r, v) => w.SetProfileValue(r, GridProfileFields.MaximumMinutes, v.Trim()),
            NotApplicable = (w, r) => w.ProfileReason(r)
        },
        new ApplicationColumn
        {
            Id = "reboot", Header = "Reboot after install", AutomationName = "Reboot behavior of the application", Width = 190,
            Choices = GridProfileFields.RebootChoices,
            Tooltip = "What the deployment type does after the install: no reboot, soft reboot, hard reboot or force restart. Inherit uses the behavior of the definition. It is stored in the Workbench profile of the application.",
            Get = (w, r) => w.ProfileValue(r, GridProfileFields.InstallReboot) is { Length: > 0 } v ? v : GridProfileFields.Inherit,
            Set = (w, r, v) => w.SetProfileValue(r, GridProfileFields.InstallReboot, v),
            NotApplicable = (w, r) => w.ProfileReason(r)
        },
        .. RequirementColumns.Build(),
        new ApplicationColumn
        {
            Id = "dpgroup", Header = "Distribution point group", AutomationName = "Distribution point group of the application", Width = 210,
            Tooltip = "The distribution point group that receives the content of this application after Package. Leave it empty to use the group of the Post Packaging page. A group here also turns distribution on for this application.",
            Get = (w, r) => w.Distribution.TryGetValue(r.ApplicationId, out var d) ? d.DpGroupName : "",
            Set = (w, r, v) => SetDistribution(w, r, dp: v.Trim(), test: null)
        },
        new ApplicationColumn
        {
            Id = "testcollection", Header = "Test collection", AutomationName = "Test collection of the application", Width = 210,
            Tooltip = "The collection that receives the test deployment of this application. Leave it empty to use the collection of the Post Packaging page. A collection here also turns the test deployment on for this application.",
            Get = (w, r) => w.Distribution.TryGetValue(r.ApplicationId, out var d) ? d.TestCollectionName : "",
            Set = (w, r, v) => SetDistribution(w, r, dp: null, test: v.Trim())
        },
        new ApplicationColumn
        {
            Id = "detection", Header = "Detection", AutomationName = "Detection method of the application", Width = 190,
            Choices = ["Inherit", "Custom in Workbench"],
            Tooltip = "Shows whether the Workbench profile of this application holds its own detection rule. The grid does not edit a detection rule: it is a list of rules. Edit it in the Workbench.",
            Get = (w, r) => w.ProfileOf(r).CustomDetection ? "Custom in Workbench" : "Inherit",
            Set = (_, _, _) => false,
            NotApplicable = (w, r) => w.ProfileOf(r).CustomDetection
                ? "This application has its own detection rule in the Workbench. Edit it there."
                : "The detection rule is edited in the Workbench, not in this grid."
        },
        new ApplicationColumn
        {
            Id = "tracked", Header = "In One Click", AutomationName = "One Click inclusion of the application", Width = 150,
            Choices = ["No", "Yes"],
            Tooltip = "Yes includes the application in a One Click run: it is checked, and staged or published as the One Click action says.",
            Get = (w, r) => w.OneClickRowOf(r.Uid) is { Tracked: true } ? "Yes" : "No",
            Set = (w, r, v) => { if (w.OneClickRowOf(r.Uid) is not { } oc || (v != "Yes" && v != "No")) return false; oc.Tracked = v == "Yes"; return true; },
            NotApplicable = (w, r) => w.OneClickRowOf(r.Uid) is null ? "One Click does not list this application." : null
        },
        new ApplicationColumn
        {
            Id = "cadence", Header = "Check interval (days)", AutomationName = "Check interval of the application", Width = 170,
            Tooltip = "The number of days that a fresh check stays valid in a One Click Report run. Leave it empty for the default interval.",
            Get = (w, r) => w.OneClickRowOf(r.Uid)?.CadenceDisplay ?? "",
            Set = (w, r, v) =>
            {
                if (w.OneClickRowOf(r.Uid) is not { } oc) return false;
                var text = v.Trim();
                if (text.Length > 0 && !(int.TryParse(text, out var days) && days > 0)) return false;
                oc.CadenceDisplay = text;
                return true;
            },
            NotApplicable = (w, r) => w.OneClickRowOf(r.Uid) is null ? "One Click does not list this application." : null
        },
        DestinationColumn("toConfigMgr", "Publish to ConfigMgr", "ConfigMgr", d => d.ConfigMgr, (d, on) => d.ConfigMgr = on, _ => null),
        DestinationColumn("toIntune", "Publish to Intune", "Intune", d => d.Intune, (d, on) => d.Intune = on, _ => null),
        DestinationColumn("toWsus", "Publish to WSUS", "WSUS", d => d.Wsus, (d, on) => d.Wsus = on,
            r => string.IsNullOrWhiteSpace(r.WsusNotSupported) ? null : "WSUS cannot publish this application (" + r.WsusNotSupported.Replace("|", ", ") + ").")
    ];
}
