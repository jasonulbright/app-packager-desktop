using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Tests;

public sealed class FirstRunDecisionTests
{
    [Fact]
    public void NoSettingsFileIsAFirstRun()
    {
        Assert.False(FirstRunDecision.ResolveCompleted(storedValue: null, settingsFileExisted: false));
        Assert.True(FirstRunDecision.IsWizardDue(false));
    }

    [Fact]
    public void AFileWrittenBeforeTheFlagCountsAsCompleted()
    {
        Assert.True(FirstRunDecision.ResolveCompleted(storedValue: null, settingsFileExisted: true));
        Assert.False(FirstRunDecision.IsWizardDue(true));
    }

    [Fact]
    public void AStoredFalseKeepsTheWizardDue()
    {
        Assert.False(FirstRunDecision.ResolveCompleted(storedValue: false, settingsFileExisted: true));
        Assert.True(FirstRunDecision.IsWizardDue(false));
    }

    [Fact]
    public void AStoredTrueSuppressesTheWizard()
    {
        Assert.True(FirstRunDecision.ResolveCompleted(storedValue: true, settingsFileExisted: true));
        Assert.False(FirstRunDecision.IsWizardDue(true));
    }

    [Fact]
    public void AnAbsentFlagOnTheSettingsObjectIsStillDue()
        => Assert.True(FirstRunDecision.IsWizardDue(null));
}
