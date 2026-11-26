using ApplicationPackagerEngine.Models;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerUI.Services;

/// <summary>
/// The confirm loop of the publish review (overhaul V1, phase 5b). After the operator confirms, the plan is read again. When the fresh plan differs,
/// the review opens again with the differences, and nothing is written until the operator confirms a plan that a fresh read matches.
/// </summary>
public static class PublishReviewGate
{
    /// <param name="plan">The plan from the last check.</param>
    /// <param name="comment">The comment the first review starts with.</param>
    /// <param name="show">Shows one review; true when the operator pressed the confirm button.</param>
    /// <param name="reread">The plan from a fresh read of the shown plan; null when the operator cancelled the read.</param>
    /// <param name="log">The log of the page.</param>
    /// <returns>The confirmed review, or null when nothing is to be published.</returns>
    public static async Task<PublishReviewViewModel?> ConfirmAsync(PublishPlan plan, string comment,
        Func<PublishReviewViewModel, bool> show, Func<PublishPlan, Task<PublishPlan?>> reread, Action<string> log)
    {
        IReadOnlyDictionary<string, string>? changes = null;
        while (true)
        {
            var review = new PublishReviewViewModel(plan, comment, changes);
            if (!show(review))
            {
                log("Publish review: closed with Back. Nothing was published.");
                return null;
            }
            comment = review.Comment;
            var fresh = await reread(plan);
            if (fresh is null)
            {
                log("Publish review: the read before the publish was cancelled. Nothing was published.");
                return null;
            }
            var differences = PublishPlanner.Changes(plan, fresh);
            if (differences.Count == 0) return review;
            log($"Publish review: the plan changed after it was confirmed. Confirmed {plan.Digest}, read {fresh.Digest}.");
            foreach (var difference in differences.Values) log("  " + difference);
            plan = fresh;
            changes = differences;
        }
    }
}
