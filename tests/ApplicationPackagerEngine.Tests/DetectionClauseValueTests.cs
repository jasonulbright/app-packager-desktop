using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The property type and the operator of a detection reach the generated ConfigMgr script
/// as bare words. Only the values the detection clause cmdlets accept may reach it.
/// </summary>
public class DetectionClauseValueTests
{
    private static StageDetection Registry(string? propertyType, string? op) => new()
    {
        Type = "RegistryKeyValue", RegistryKeyRelative = @"SOFTWARE\Vendor\App", ValueName = "DisplayVersion",
        PropertyType = propertyType, Operator = op, ExpectedValue = "1.0"
    };

    private static StageDetection FileClause(string? propertyType, string? op) => new()
    {
        Type = "File", FilePath = @"C:\Program Files\App", FileName = "app.exe", PropertyType = propertyType, Operator = op, ExpectedValue = "1.0"
    };

    [Theory]
    [InlineData("Version", "GreaterEquals", "-PropertyType Version -Value -ExpressionOperator GreaterEquals")]
    [InlineData("String", "BeginsWith", "-PropertyType String -Value -ExpressionOperator BeginsWith")]
    [InlineData("integer", "isequals", "-PropertyType Integer -Value -ExpressionOperator IsEquals")]
    [InlineData("String", "NotContains", "-PropertyType String -Value -ExpressionOperator NotContains")]
    [InlineData(null, null, "-PropertyType String -Value -ExpressionOperator IsEquals")]
    [InlineData(" ", "", "-PropertyType String -Value -ExpressionOperator IsEquals")]
    public void ARegistryValueClauseCarriesAnAcceptedValueInTheCmdletSpelling(string? propertyType, string? op, string expected) =>
        Assert.Contains(expected, MecmTarget.BuildDetectionClauseBlockPs(Registry(propertyType, op)));

    [Theory]
    [InlineData("Version", "GreaterEquals", "-PropertyType Version -Value -ExpressionOperator GreaterEquals")]
    [InlineData("size", "lessthan", "-PropertyType Size -Value -ExpressionOperator LessThan")]
    [InlineData("DateModified", "Between", "-PropertyType DateModified -Value -ExpressionOperator Between")]
    [InlineData(null, null, "-PropertyType Version -Value -ExpressionOperator GreaterEquals")]
    public void AFileClauseCarriesAnAcceptedValueInTheCmdletSpelling(string? propertyType, string? op, string expected) =>
        Assert.Contains(expected, MecmTarget.BuildDetectionClauseBlockPs(FileClause(propertyType, op)));

    [Theory]
    [InlineData("Version; Remove-Item C:\\x -Recurse", "GreaterEquals")]
    [InlineData("Version", "GreaterEquals -ExpectedValue '0'; Start-Process calc #")]
    [InlineData("Version", "$(whoami)")]
    [InlineData("DateCreated", "IsEquals")]
    [InlineData("Size", "IsEquals")]
    [InlineData("Existence", "IsEquals")]
    public void ARegistryValueClauseRefusesAnyOtherValue(string propertyType, string op)
    {
        var error = Assert.Throws<InvalidOperationException>(() => MecmTarget.BuildDetectionClauseBlockPs(Registry(propertyType, op)));
        Assert.Contains("Detection", error.Message);
    }

    [Theory]
    [InlineData("Version -Existence; calc", "GreaterEquals")]
    [InlineData("Version", "BeginsWith")]
    [InlineData("Version", "Contains")]
    [InlineData("String", "IsEquals")]
    [InlineData("Version", "GreaterEquals\r\nStart-Process calc")]
    public void AFileClauseRefusesAnyOtherValue(string propertyType, string op) =>
        Assert.Throws<InvalidOperationException>(() => MecmTarget.BuildDetectionClauseBlockPs(FileClause(propertyType, op)));

    [Fact]
    public void AClauseInsideACompoundDetectionIsCheckedToo()
    {
        var compound = new StageDetection
        {
            Type = "Compound", Clauses = [Registry("Version", "GreaterEquals"), Registry("Version", "GreaterEquals; calc")]
        };
        Assert.Throws<InvalidOperationException>(() => MecmTarget.BuildDetectionClauseBlockPs(compound));
    }

    [Fact]
    public void AFileExistenceClauseStillNeedsNoOperator() =>
        Assert.Contains("-Existence", MecmTarget.BuildDetectionClauseBlockPs(FileClause("Existence", null)));

    [Fact]
    public void EveryShippedDefinitionStagesADetectionTheScriptAccepts()
    {
        foreach (var path in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Definitions"), "*.json"))
        {
            var def = DefinitionFixture.Load(Path.GetFileName(path));
            foreach (var det in new[] { def.Detection }.Concat(def.Detection?.Clauses ?? []))
            {
                if (det is null || det.Type is not ("File" or "RegistryKeyValue")) continue;
                var staged = new StageDetection
                {
                    Type = det.Type, FilePath = "C:\\x", FileName = "x.exe", RegistryKeyRelative = "SOFTWARE\\x", ValueName = "v",
                    PropertyType = det.PropertyType, Operator = det.Operator, ExpectedValue = "1.0"
                };
                var error = Record.Exception(() => MecmTarget.BuildDetectionClauseBlockPs(staged));
                Assert.True(error is null, $"{Path.GetFileName(path)}: {error?.Message}");
            }
        }
    }
}
