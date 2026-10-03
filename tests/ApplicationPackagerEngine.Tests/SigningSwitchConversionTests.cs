using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models.Signing;
using ApplicationPackagerEngine.Services.Workbench;

namespace ApplicationPackagerEngine.Tests;

/// <summary>The run-snapshot gate and the signing policy read a switch value the same way.</summary>
public sealed class SigningSwitchConversionTests
{
    [Theory]
    [InlineData("\"false\"", false)]
    [InlineData("\"0\"", false)]
    [InlineData("\"off\"", false)]
    [InlineData("\"no\"", false)]
    [InlineData("\"OFF\"", false)]
    [InlineData("\"\"", false)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData("null", false)]
    [InlineData("\"maybe\"", false)]
    [InlineData("\"true\"", true)]
    [InlineData("\"on\"", true)]
    [InlineData("\"Yes\"", true)]
    [InlineData("\"1\"", true)]
    [InlineData("true", true)]
    [InlineData("1", true)]
    public void IsSigningRequested_AgreesWithThePolicyReader(string value, bool expected)
    {
        foreach (var name in SigningPolicySwitches.Names)
        {
            var policy = JsonNode.Parse($$"""{ "{{name}}": {{value}} }""");
            var read = SigningPolicy.FromJsonNode(policy);
            var any = read.AnySign || read.RequireDetection || read.RequireRequirements || read.RequireDeployment;
            Assert.Equal(expected, any);
            Assert.Equal(expected, SigningPolicySwitches.IsSigningRequested(policy));
        }
    }

    [Fact]
    public void AnInvalidStoreLocation_DoesNotHideASwitch() =>
        Assert.True(SigningPolicySwitches.IsSigningRequested(JsonNode.Parse("""{ "SignDeployment": "on", "StoreLocation": "Elsewhere" }""")));
}
