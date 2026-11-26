using System.Reflection;
using ApplicationPackagerEngine.Models;
using System.Runtime.CompilerServices;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerEngine.Tests;

/// <summary>A Packages view model with a real settings store and scripted publish windows, for the parts of Publish All that run before a write.</summary>
internal static class PublishRunKit
{
    internal sealed class Dialogs : IPublishDialogs
    {
        /// <summary>One answer for each review window, in order; a review past the last one is answered with Back.</summary>
        public Queue<Func<PublishReviewViewModel, bool>> Answers { get; } = new();
        public List<PublishReviewViewModel> Reviews { get; } = [];
        public List<(string Title, string Message)> Messages { get; } = [];
        public List<PublishResultsViewModel> Results { get; } = [];

        public bool ShowPublishReview(PublishReviewViewModel viewModel)
        {
            Reviews.Add(viewModel);
            return Answers.TryDequeue(out var answer) && answer(viewModel);
        }

        public void ShowPublishResults(PublishResultsViewModel viewModel) => Results.Add(viewModel);
        public void ShowMessage(string title, string message) => Messages.Add((title, message));
        public void ShowWsusUpdates(WsusSettings settings) => throw new InvalidOperationException("Not part of a publish.");
        public void ShowOneClickPlan(OneClickPlanViewModel viewModel) => throw new InvalidOperationException("Not part of a publish.");
        public ChangePlanOutcome ShowChangePlan(ChangePlan plan, ChangePlanRun run, ChangePlanRun? undo = null) =>
            throw new InvalidOperationException("Not part of a publish.");
    }

    internal static void Set(PackagesViewModel viewModel, string field, object value) =>
        typeof(PackagesViewModel).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(viewModel, value);

    /// <summary>
    /// A view model without its constructor: the services that a publish reaches only after the review are absent, so a run that writes without
    /// a review fails the test.
    /// </summary>
    internal static PackagesViewModel Create(SettingsService settings, Dialogs dialogs, params PackagerInfo[] rows)
    {
        var viewModel = (PackagesViewModel)RuntimeHelpers.GetUninitializedObject(typeof(PackagesViewModel));
        // The constructor makes the commands, and a change of the run state notifies them.
        foreach (var field in typeof(PackagesViewModel).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                     .Where(f => f.Name.EndsWith("Command>k__BackingField", StringComparison.Ordinal)))
            field.SetValue(viewModel, new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(() => Task.CompletedTask));
        Set(viewModel, "_settingsService", settings);
        Set(viewModel, "_publishDialogs", dialogs);
        viewModel.Packagers = new(rows);
        viewModel.FilteredPackagers = new(rows);
        viewModel.LogText = "";
        viewModel.Comment = "";
        return viewModel;
    }

    internal static Task PublishAllAsync(PackagesViewModel viewModel) =>
        (Task)typeof(PackagesViewModel).GetMethod("PublishAllAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(viewModel, null)!;
}
