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
        "It does not supersede an older application, does not deploy to a production collection, and distributes the content or " +
        "deploys to the test collection only when Settings turn those steps on.";

    public const string IntuneSentence =
        "Intune: builds the .intunewin file, then creates or updates the Win32 app. It does not assign the app to a group, so no " +
        "device receives it until you assign it in the Intune admin center.";

    public const string WsusSentence =
        "WSUS: publishes an update for an installer that a computer already has. It cannot install the product on a computer " +
        "that does not have it (no first install through WSUS), and it does not approve the update.";

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
