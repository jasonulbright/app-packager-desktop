using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Values that the ConfigMgr cmdlets refuse must be refused before the site is touched, with a message that names the value
/// and what to use instead. Values that the format page allows must be sent in the form the cmdlet takes.
/// </summary>
public sealed class ConfigMgrPrecheckTests
{
    private static StageManifest Manifest(StageDetection detection) => new()
    {
        AppName = "App", Publisher = "Vendor", SoftwareVersion = "1.0", Detection = detection
    };

    private static string Script(StageDetection detection) =>
        MecmTarget.BuildPackageScript(Manifest(detection), "App", @"\\server\share\Apps\Vendor\App\1.0", "P01", "comment", 15, 30);

    [Theory]
    [InlineData("Between")]
    [InlineData("OneOf")]
    [InlineData("NoneOf")]
    public void AListOperatorIsRefusedForARegistryValueBeforeAnythingIsWritten(string op)
    {
        var detection = new StageDetection { Type = "RegistryKeyValue", RegistryKeyRelative = @"SOFTWARE\Vendor\App", ValueName = "V", ExpectedValue = "1", Operator = op };

        var ex = Assert.Throws<InvalidOperationException>(() => Script(detection));

        Assert.Contains($"Detection RegistryKeyValue Operator '{op}' is not a value the ConfigMgr cmdlet accepts", ex.Message);
        Assert.DoesNotContain("Between", ex.Message.Split("Use one of:")[1]);
        Assert.DoesNotContain("OneOf", ex.Message.Split("Use one of:")[1]);
        Assert.DoesNotContain("NoneOf", ex.Message.Split("Use one of:")[1]);
    }

    [Theory]
    [InlineData("Between")]
    [InlineData("OneOf")]
    [InlineData("NoneOf")]
    public void AListOperatorIsRefusedForAFileBeforeAnythingIsWritten(string op)
    {
        var detection = new StageDetection { Type = "File", FilePath = @"C:\Program Files\App", FileName = "app.exe", PropertyType = "Version", ExpectedValue = "1", Operator = op };

        var ex = Assert.Throws<InvalidOperationException>(() => Script(detection));

        Assert.Contains($"Detection File Operator '{op}' is not a value the ConfigMgr cmdlet accepts", ex.Message);
    }

    [Theory]
    [InlineData("IsEquals")]
    [InlineData("GreaterEquals")]
    [InlineData("BeginsWith")]
    public void TheOperatorsThatTakeOneValueStillWork(string op)
    {
        var detection = new StageDetection { Type = "RegistryKeyValue", RegistryKeyRelative = @"SOFTWARE\Vendor\App", ValueName = "V", PropertyType = "String", ExpectedValue = "1", Operator = op };

        Assert.Contains($"-ExpressionOperator {op} -ExpectedValue '1'", Script(detection));
    }

    [Fact]
    public void ARegistryValueExistenceIsSentWithTheNameTheTypeAndTheExistenceSwitch()
    {
        var detection = new StageDetection { Type = "RegistryKeyValue", RegistryKeyRelative = @"SOFTWARE\Vendor\App", ValueName = "InstallDir", PropertyType = "Existence" };

        var script = Script(detection);

        Assert.Contains(@"$clause = New-CMDetectionClauseRegistryKeyValue -Hive LocalMachine -KeyName 'SOFTWARE\Vendor\App' -ValueName 'InstallDir' -PropertyType String -Existence", script);
        Assert.DoesNotContain("-ExpectedValue", script.Split("$clause = New-CMDetectionClauseRegistryKeyValue")[1].Split('\n')[0]);
    }

    [Fact]
    public void ARegistryValueExistenceKeepsTheSixtyFourBitSwitch()
    {
        var detection = new StageDetection { Type = "RegistryKeyValue", RegistryKeyRelative = @"SOFTWARE\Vendor\App", ValueName = "InstallDir", PropertyType = "Existence", Is64Bit = true };

        Assert.Contains("-PropertyType String -Existence -Is64Bit", Script(detection));
    }
}