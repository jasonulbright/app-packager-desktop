using ApplicationPackagerEngine.Services.Supersedence;

namespace ApplicationPackagerEngine.Tests;

public sealed class InstalledValueRecorderTests
{
    [Theory]
    [InlineData("1", true)]
    [InlineData("1.2.3.4", true)]
    [InlineData("2024.1-beta+5", true)]
    [InlineData("", false)]
    [InlineData("v1.2", false)]
    [InlineData("1.2 3", false)]
    [InlineData("1.2\n", false)]
    [InlineData("1;calc", false)]
    public void OnlyAVersionTextIsAValue(string text, bool valid) => Assert.Equal(valid, InstalledValueRecorder.IsValidValue(text));

    [Fact]
    public void TheValueIsStoredUnderTheKeyThatTheGuardReads()
    {
        var settings = new SupersedenceSettings();

        var key = InstalledValueRecorder.Record(settings, "catalog:abc", "1.2.3", "1.2.3.4");

        Assert.Equal(ExactGuard.RecordKey("catalog:abc", "1.2.3"), key);
        Assert.Equal("1.2.3.4", settings.InstalledValues[key]);
        InstalledValueRecorder.Record(settings, "catalog:abc", "1.2.3", "1.2.3.5");
        Assert.Single(settings.InstalledValues);
        Assert.Equal("1.2.3.5", settings.InstalledValues[key]);
    }

    [Fact]
    public void AnAcceptanceIsStoredOnceAndPerVersion()
    {
        var settings = new SupersedenceSettings();

        InstalledValueRecorder.AcceptBlock(settings, "catalog:abc", "1.2.3");
        InstalledValueRecorder.AcceptBlock(settings, "CATALOG:ABC", "1.2.3");
        InstalledValueRecorder.AcceptBlock(settings, "catalog:abc", "1.2.4");

        Assert.Equal(2, settings.AcceptedExactBlocks.Count);
    }

    [Fact]
    public void ABadVersionOrValueOrTitleIsRefusedAndNothingIsStored()
    {
        var settings = new SupersedenceSettings();

        Assert.Throws<InvalidOperationException>(() => InstalledValueRecorder.Record(settings, "catalog:abc", "latest", "1.0"));
        Assert.Throws<InvalidOperationException>(() => InstalledValueRecorder.Record(settings, "catalog:abc", "1.0", "newest"));
        Assert.Throws<InvalidOperationException>(() => InstalledValueRecorder.Record(settings, " ", "1.0", "1.0"));
        Assert.Throws<InvalidOperationException>(() => InstalledValueRecorder.AcceptBlock(settings, "catalog:abc", "x"));
        Assert.Empty(settings.InstalledValues);
        Assert.Empty(settings.AcceptedExactBlocks);
    }
}
