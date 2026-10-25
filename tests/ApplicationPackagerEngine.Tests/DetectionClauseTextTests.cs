using ApplicationPackagerEngine.Models;
using ApplicationPackagerUI.ViewModels.Pages;

namespace ApplicationPackagerEngine.Tests;

public sealed class DetectionClauseTextTests
{
    [Theory]
    [InlineData("'installed'", "Script (1 line)")]
    [InlineData("'installed'\r\n", "Script (1 line)")]
    [InlineData("$a = 1\n$b = 2\n'installed'", "Script (3 lines)")]
    [InlineData("$a = 1\r\n$b = 2\r\n'installed'\r\n", "Script (3 lines)")]
    [InlineData("'x'\n'y'", "Script (2 lines)")]
    [InlineData("", "Script (0 lines)")]
    public void AScriptDescriptionCountsItsLinesInPlainText(string script, string expected) =>
        Assert.Equal(expected, DetectionClauseText.Describe(new StageDetection { Type = "Script", ScriptText = script }));
}
