namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The uninstaller that the installer writes is signed. Windows Defender removes an unsigned Uninstall.exe together
/// with its uninstall registry entry (Trojan:Win32/Bearfoos.A!ml, seen on a lab machine), so the release builds the
/// uninstaller first, signs it and embeds the signed file in the installer. These tests read the build files; the
/// release workflow runs the passes.
/// </summary>
public sealed class SignedUninstallerBuildTests
{
    private static string RepoFile(params string[] parts)
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "installer", "AppPackagerDesktop.nsi"))) folder = folder.Parent;
        Assert.NotNull(folder);
        return Path.Combine([folder.FullName, .. parts]);
    }

    private static string Text(params string[] parts) => File.ReadAllText(RepoFile(parts));

    [Fact]
    public void TheInstallerScriptHasAGeneratorPassAndACarryPass()
    {
        var nsi = Text("installer", "AppPackagerDesktop.nsi");

        // Pass 1: a generator that writes the uninstaller to the given path and ends.
        Assert.Contains("!ifdef BUILD_UNINSTALLER", nsi);
        Assert.Contains("WriteUninstaller \"${UNINSTALLER_FILE}\"", nsi);
        Assert.Contains("!error \"UNINSTALLER_FILE is required", nsi);

        // Pass 2: the installer carries the signed file as Uninstall.exe.
        Assert.Contains("!ifdef SIGNED_UNINSTALLER", nsi);
        Assert.Contains("File \"/oname=$INSTDIR\\Uninstall.exe\" \"${SIGNED_UNINSTALLER}\"", nsi);

        // A local build keeps its own uninstaller.
        Assert.Contains("WriteUninstaller \"$INSTDIR\\Uninstall.exe\"", nsi);
    }

    [Fact]
    public void TheInstallerBuildHasTheTwoPasses()
    {
        var build = Text("tools", "build-installer.ps1");

        Assert.Contains("[switch]$UninstallerOnly", build);
        Assert.Contains("[string]$SignedUninstaller", build);
        Assert.Contains("/DBUILD_UNINSTALLER", build);
        Assert.Contains("/DUNINSTALLER_FILE=", build);
        Assert.Contains("/DSIGNED_UNINSTALLER=", build);
        // The uninstaller goes to a folder of its own, outside the stage, so it is never packed as a stage file.
        Assert.Contains("Join-Path $OutputDir 'uninstaller'", build);
    }

    private static int IndexOf(string yaml, string stepName)
    {
        var index = yaml.IndexOf($"- name: {stepName}", StringComparison.Ordinal);
        Assert.True(index >= 0, $"The release workflow has no step '{stepName}'.");
        return index;
    }

    [Fact]
    public void TheReleaseBuildsAndSignsTheUninstallerBeforeItBuildsTheInstaller()
    {
        var yaml = Text(".github", "workflows", "release.yml");

        var signStage = IndexOf(yaml, "Sign the published files");
        var buildUninstaller = IndexOf(yaml, "Build the uninstaller");
        var signUninstaller = IndexOf(yaml, "Sign the uninstaller");
        var buildInstaller = IndexOf(yaml, "Build the installer");
        var signInstaller = IndexOf(yaml, "Sign the installer");
        var verify = IndexOf(yaml, "Verify the signatures");

        Assert.True(signStage < buildUninstaller && buildUninstaller < signUninstaller && signUninstaller < buildInstaller
            && buildInstaller < signInstaller && signInstaller < verify, "The steps are not in the order stage, uninstaller, installer.");

        var buildUninstallerLine = yaml[buildUninstaller..signUninstaller];
        Assert.Contains("-UninstallerOnly", buildUninstallerLine);

        var signUninstallerBlock = yaml[signUninstaller..buildInstaller];
        Assert.Contains("azure/artifact-signing-action@", signUninstallerBlock);
        Assert.Contains("publish\\uninstaller", signUninstallerBlock);

        var buildInstallerBlock = yaml[buildInstaller..signInstaller];
        Assert.Contains("-SignedUninstaller", buildInstallerBlock);
        Assert.Contains("uninstaller\\Uninstall.exe", buildInstallerBlock);
    }

    [Fact]
    public void ADryRunBuildsAndSignsButCreatesNoTagAndNoRelease()
    {
        var yaml = Text(".github", "workflows", "release.yml");

        Assert.Contains("dry_run:", yaml);

        // The tag and the release are skipped; the files are kept as an artifact.
        foreach (var step in new[] { "Tag the release commit", "Publish the GitHub release" })
        {
            var start = IndexOf(yaml, step);
            var header = yaml[start..Math.Min(yaml.Length, start + 120)];
            Assert.Contains("if: ${{ !inputs.dry_run }}", header);
        }
        var keep = IndexOf(yaml, "Keep the installer and the zip (dry run)");
        Assert.Contains("if: ${{ inputs.dry_run }}", yaml[keep..Math.Min(yaml.Length, keep + 120)]);

        // The version and tag checks do not apply to a build of a version that is already released.
        Assert.Contains("DRY_RUN: ${{ inputs.dry_run }}", yaml);
        Assert.Contains("if ($env:DRY_RUN -ne 'true')", yaml);
    }

    [Fact]
    public void ThePackageStepRemovesTheUninstallerFolder() =>
        Assert.Contains("'uninstaller'", Text("tools", "build-release.ps1"));
}
