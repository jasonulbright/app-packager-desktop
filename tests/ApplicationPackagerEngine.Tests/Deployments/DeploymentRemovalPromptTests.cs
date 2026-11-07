using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Windows;
using ApplicationPackagerUI.Views.Windows;
using Wpf.Ui.Appearance;
using static ApplicationPackagerEngine.Tests.Compliance.RenderKit;
using Application = System.Windows.Application;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace ApplicationPackagerEngine.Tests.Deployments;

/// <summary>The removal question of an Undo in a real modal window over a real change plan window.</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class DeploymentRemovalPromptTests
{
    private const string Question = "Remove the deployment 7-Zip from Pilot Workstations (LAB00010)? Software that clients already installed stays installed.";

    private sealed record Seen(Window? Owner, bool CancelFocused, bool CancelIsDefault, bool RemoveIsDefault, string Text)
    {
        /// <summary>True when the action closed the question by itself, before the test closes it.</summary>
        public bool ClosedByAction { get; set; }
    }

    /// <summary>Sends the key through the input manager, so the access keys of IsDefault and IsCancel see it as a real key press.</summary>
    private static void Press(Key key)
    {
        var target = (UIElement)Keyboard.FocusedElement;
        InputManager.Current.ProcessInput(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(target)!, 0, key) { RoutedEvent = Keyboard.KeyDownEvent });
    }

    /// <summary>Asks the question over the plan window; <paramref name="act"/> runs once the question is open, and a question still open after it is closed.</summary>
    private static (bool Result, Seen Seen) Ask(Window plan, Action<RemovalConfirmWindow> act)
    {
        Seen? seen = null;
        plan.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            var prompt = Application.Current.Windows.OfType<RemovalConfirmWindow>().Single();
            try
            {
                var cancel = ById<ButtonBase>(prompt, "btnRemovalCancel");
                var remove = ById<ButtonBase>(prompt, "btnRemovalConfirm");
                seen = new Seen(prompt.Owner, ReferenceEquals(Keyboard.FocusedElement, cancel), ((System.Windows.Controls.Button)cancel).IsDefault,
                    ((System.Windows.Controls.Button)remove).IsDefault, ById<System.Windows.Controls.TextBlock>(prompt, "txtRemovalQuestion").Text);
                act(prompt);
                seen.ClosedByAction = !prompt.IsVisible;
            }
            finally
            {
                prompt.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => { if (prompt.IsVisible) prompt.Close(); });
            }
        });
        var owner = RemovalConfirmWindow.OwnerFor(Application.Current.Windows.OfType<Window>(), null);
        var result = RemovalConfirmWindow.Ask(owner, Question);
        Assert.NotNull(seen);
        return (result, seen!);
    }

    private static ChangePlanWindow PlanWindow() =>
        new(new ChangePlanViewModel(new ChangePlan("Deploy 7-Zip", "Create deployment", [new ChangePlanItem("Create deployment", "7-Zip", null)]),
            (_, _, _) => Task.FromResult(new ChangePlanResult([]))));

    [WindowTheory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheQuestionIsOwnedByThePlanWindowFocusesCancelAndTakesEscapeAndEnterFromItsFirstAppearance(ApplicationTheme theme)
    {
        WithApplication(theme, () =>
        {
            var plan = PlanWindow();
            Show(plan);
            try
            {
                foreach (var key in new[] { Key.Escape, Key.Enter, Key.Escape, Key.Escape })
                {
                    var (result, seen) = Ask(plan, _ => Press(key));
                    Assert.False(result, $"{key} confirmed the removal.");
                    Assert.True(seen.ClosedByAction, $"{key} did not close the question.");
                    Assert.True(plan.IsVisible, $"{key} reached the plan window.");
                    Assert.Same(plan, seen.Owner);
                    Assert.True(seen.CancelFocused, $"Cancel had no focus when the question opened ({key}).");
                    Assert.True(seen.CancelIsDefault);
                    Assert.False(seen.RemoveIsDefault);
                    Assert.Equal(Question, seen.Text);
                }

                var (confirmed, clicked) = Ask(plan, prompt => ((System.Windows.Controls.Button)ById<ButtonBase>(prompt, "btnRemovalConfirm"))
                    .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)));
                Assert.True(confirmed);
                Assert.True(clicked.ClosedByAction);
            }
            finally { plan.Close(); }
        });
    }

    [WindowTheory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void AQuestionThatIsClosedWithoutAChoiceIsDeclined(ApplicationTheme theme)
    {
        WithApplication(theme, () =>
        {
            var plan = PlanWindow();
            Show(plan);
            try
            {
                var (result, seen) = Ask(plan, prompt => prompt.Close());
                Assert.False(result);
                Assert.True(seen.ClosedByAction);
                Assert.True(seen.CancelFocused);
            }
            finally { plan.Close(); }
        });
    }
}