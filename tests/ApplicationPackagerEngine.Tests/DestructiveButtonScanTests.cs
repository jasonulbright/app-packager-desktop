using System.Xml.Linq;
using ApplicationPackagerEngine.Tests.Compliance;

namespace ApplicationPackagerEngine.Tests;

/// <summary>A button that opens the plan of a destructive change uses the danger appearance, so it never looks like an approval.</summary>
public sealed class DestructiveButtonScanTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    /// <summary>The commands whose plan has isDestructive set.</summary>
    private static readonly string[] DestructiveCommands =
    [
        "RemoveScriptCommand", "RemoveLeftoverCommand", "RemoveFromDpCommand", "RemoveFailuresCommand", "RemoveDeploymentCommand",
        "RemoveWindowsCommand", "RemoveCollectionCommand", "DeleteDeploymentsCommand"
    ];

    [Fact]
    public void EveryButtonOfADestructiveCommandUsesTheDangerAppearance()
    {
        var views = Path.Combine(CatalogServiceTests.RepositoryRoot(), "src", "ApplicationPackagerUI", "Views");
        var found = new List<string>();
        var wrong = new List<string>();
        foreach (var file in Directory.EnumerateFiles(views, "*.xaml", SearchOption.AllDirectories))
            foreach (var button in XDocument.Load(file, LoadOptions.SetLineInfo).Descendants().Where(e => e.Name.LocalName == "Button"))
            {
                var command = (string?)button.Attribute("Command") ?? "";
                var name = DestructiveCommands.FirstOrDefault(c => command.Contains(c, StringComparison.Ordinal));
                if (name is null) continue;
                found.Add(name);
                if ((string?)button.Attribute("Appearance") != "Danger") wrong.Add($"{Path.GetFileName(file)}:{((System.Xml.IXmlLineInfo)button).LineNumber} {name}");
            }
        Assert.Equal(DestructiveCommands.Order(), found.Distinct().Order());
        Assert.True(wrong.Count == 0, "These buttons open a destructive plan without the danger appearance: " + string.Join(", ", wrong));
    }
}
