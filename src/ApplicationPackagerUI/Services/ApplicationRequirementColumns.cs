namespace ApplicationPackagerUI.Services;

/// <summary>
/// The columns of the Application Settings grid for the requirement templates: free disk space, memory and the minimum operating system
/// build. Each writes one requirement operation of the Workbench profile (see <see cref="GridRequirementFields"/>); an empty cell removes it.
/// <see cref="ApplicationColumns"/> adds them to the registry as one block.
/// </summary>
public static class RequirementColumns
{
    private const string Stored = " Leave it empty for no requirement. It is stored as a requirement operation in the Workbench profile of the application.";

    private static ApplicationColumn Column(string id, string header, string path, string tooltip) => new()
    {
        Id = id, Header = header, AutomationName = $"{header} of the application", Width = 190,
        Tooltip = tooltip + Stored,
        Get = (w, r) => w.ProfileValue(r, path),
        Set = (w, r, v) => w.SetProfileValue(r, path, v.Trim()),
        NotApplicable = (w, r) => w.ProfileReason(r)
    };

    public static IReadOnlyList<ApplicationColumn> Build() =>
    [
        Column("diskSpace", "Free disk space (MB)", GridRequirementFields.DiskSpace,
            "The free space in MB that the system drive needs for the install. The deployment type gets a requirement with the Disk space global condition (at least this value). Intune gets it as the minimum free disk space of the app."),
        Column("memory", "Memory (MB)", GridRequirementFields.Memory,
            "The physical memory in MB that the computer needs. The deployment type gets a requirement with the Total physical memory global condition (at least this value). Intune gets it as the minimum memory of the app."),
        Column("osBuild", "OS build (minimum)", GridRequirementFields.OsBuild,
            "The lowest operating system build that the deployment type accepts, for example 19045 or 22631. The deployment type gets a requirement with a global condition on the version of the operating system (at least 10.0.build). Intune gets it as the minimum Windows release, or as a requirement script when the build is not the build of a release.")
    ];
}
