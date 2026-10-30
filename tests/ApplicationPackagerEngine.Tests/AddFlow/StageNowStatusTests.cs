using ApplicationPackagerUI.ViewModels.Pages;

namespace ApplicationPackagerEngine.Tests.AddFlow;

/// <summary>The status line Stage now shows on the result panel names why the stage failed.</summary>
public sealed class StageNowStatusTests
{
    [Fact]
    public void AFailedStage_ShowsTheFirstLineOfTheReason()
    {
        var line = PackagesViewModel.StageNowStatus("Stage complete. 0 of 1 staged, 1 failed.",
            "The installer C:\\a.msi changed after Add from file saved it.\r\nsecond line");

        Assert.Equal("Stage complete. 0 of 1 staged, 1 failed. Reason: The installer C:\\a.msi changed after Add from file saved it.", line);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \r\n ")]
    public void AStageWithNoReason_ShowsTheStatusOnly(string? failure)
    {
        Assert.Equal("Stage complete. 1 of 1 staged.", PackagesViewModel.StageNowStatus("Stage complete. 1 of 1 staged.", failure));
    }
}
