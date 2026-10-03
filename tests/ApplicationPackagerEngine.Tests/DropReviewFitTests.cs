using ApplicationPackagerUI.Controls;

namespace ApplicationPackagerEngine.Tests;

public class DropReviewFitTests
{
    [Theory]
    [InlineData(800)]
    [InlineData(1200)]
    public void TheFieldsLeaveTheReservedHeightOfTheWindow(double windowHeight)
    {
        Assert.Equal(windowHeight, DropReviewPanel.FieldsMaxHeight(windowHeight) + DropReviewPanel.ReservedHeight);
    }

    [Fact]
    public void ASmallWindowKeepsAMinimumFieldHeightAndAnUnmeasuredOneNoCap()
    {
        Assert.Equal(DropReviewPanel.MinFieldsHeight, DropReviewPanel.FieldsMaxHeight(400));
        Assert.Equal(double.PositiveInfinity, DropReviewPanel.FieldsMaxHeight(double.NaN));
    }
}
