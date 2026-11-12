using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// What a publish to each target does and does not do, in plain sentences, shown before the publish starts. The sentences state
/// the scope of the program so that nobody expects a deployment, an assignment or a first install that the program does not make.
/// </summary>
public static class PublishPreflight
{
    public const string ConfigMgrSentence =
        "ConfigMgr: creates or updates the application and its deployment types and copies the content to the share. " +
        "It does not supersede an older application. It distributes the content, and deploys (Available, not Required) to the " +
        "test collection that Settings name, only when Settings turn those steps on.";

    public const string IntuneSentence =
        "Intune: builds or reuses the .intunewin file, then creates or updates the Win32 app. It does not create or change " +
        "assignments. A new app reaches no device until you assign it in the Intune admin center; an update reaches the devices " +
        "to which the app is already assigned.";

    public const string WsusSentence =
        "WSUS: publishes an update for an installer that a computer already has. It cannot install the product on a computer " +
        "that does not have it (no first install through WSUS). It approves the update only for the approval group in Settings " +
        "(none when the box is empty), marks earlier versions as superseded, and declines them only when Settings turn that on.";

    /// <summary>The sentences of every target that the deployment target publishes to, in a fixed order.</summary>
    public static IReadOnlyList<string> Lines(string? deploymentTarget)
    {
        var lines = new List<string>();
        if (!DeploymentTargets.SkipsMecm(deploymentTarget)) lines.Add(ConfigMgrSentence);
        if (DeploymentTargets.PublishesToIntune(deploymentTarget)) lines.Add(IntuneSentence);
        if (DeploymentTargets.PublishesToWsus(deploymentTarget)) lines.Add(WsusSentence);
        return lines;
    }
}
