using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.ViewModels.Pages;

namespace ApplicationPackagerEngine.Tests.Deployments;

/// <summary>The plan text of a create over every option combination: each fact once, plain labels, correct articles.</summary>
public sealed partial class DeploymentPlanTextTests
{
    private static readonly DeploymentPageHarness H = new();

    // The lines the service puts in Impact for a create; the page states each of these facts itself.
    private static IReadOnlyList<string> ServiceImpact(DeploymentOptions o, bool distributes) =>
    [
        .. distributes ? new[] { "First requests content distribution of the object to 1 DP group: All DPs.", "Then creates the deployment only when the content check passes." } : [],
        $"Creates a {o.Purpose} deployment of the object to collection Pilot Workstations (LAB00010) (3 members now).",
        $"Fast network: {o.NetworkBehavior}. Slow network: {o.SlowNetworkBehavior}.",
        "Undo removes the deployment. Software that clients already installed stays installed."
    ];

    private static IEnumerable<(DeploymentObjectType Type, DeploymentOptions Options, bool Distributes, bool Backdated)> Combinations()
    {
        var index = 0;
        foreach (var type in Enum.GetValues<DeploymentObjectType>())
        foreach (var purpose in new[] { DeploymentText.Available, DeploymentText.Required })
        foreach (var utc in new[] { false, true })
        foreach (var distributes in new[] { false, true })
        foreach (var backdated in new[] { false, true })
        for (var flags = 0; flags < 1 << 9; flags++)
        {
            bool Bit(int n) => (flags & (1 << n)) != 0;
            index++;
            DeploymentChoice Pick(IReadOnlyList<DeploymentChoice> list) => list[index % list.Count];
            var notification = Pick(DeploymentText.Notifications).Value;
            if (purpose == DeploymentText.Available && notification == DeploymentText.HideAll) notification = DeploymentText.Notifications[0].Value;
            var available = new DateTime(2026, 10, 20, backdated ? 7 : 10, 0, 0);
            var options = new DeploymentOptions(Purpose: purpose, AvailableTime: available, Deadline: purpose == DeploymentText.Required ? available.AddDays(2) : null,
                UtcBasis: utc, UserNotification: notification, OverrideServiceWindow: Bit(0), RebootOutsideServiceWindow: Bit(1), AllowMetered: Bit(2),
                NetworkBehavior: type == DeploymentObjectType.TaskSequence ? Pick(DeploymentText.TaskSequenceContents).Value : type == DeploymentObjectType.Package ? Pick(DeploymentText.NetworkBehaviors).Value : "",
                RerunBehavior: type is DeploymentObjectType.Package or DeploymentObjectType.TaskSequence ? Pick(DeploymentText.RerunBehaviors).Value : "",
                ShowProgress: Bit(3), AllowFallback: Bit(4), FallbackToDp: Bit(5), PostRebootScan: Bit(6), SuppressRestart: Bit(7),
                SlowNetworkBehavior: Pick(DeploymentText.SlowNetworkBehaviors).Value, TaskSequenceAvailability: Pick(DeploymentText.TaskSequenceAvailabilities).Value,
                AllowMicrosoftUpdate: Bit(8));
            yield return (type, options, distributes && type != DeploymentObjectType.SoftwareUpdateGroup, backdated);
        }
    }

    private static DeploymentChange Create(DeploymentObjectType type, DeploymentOptions options, bool distributes)
    {
        var obj = type switch
        {
            DeploymentObjectType.Application => H.SevenZip,
            DeploymentObjectType.Package => H.Office with { Program = "Install" },
            DeploymentObjectType.TaskSequence => H.Win11,
            _ => H.Updates with { UpdateCount = 12 }
        };
        return new DeploymentChange(Kind: DeploymentChangeKind.Create, Object: obj, Collection: new DeploymentCollection("LAB00010", "Pilot Workstations", "", 3), Options: options,
            DpGroups: distributes ? ["All DPs"] : [], CreateNowContentDistributing: false, RequiredConfirmed: false, BackdatedAcknowledged: false, Existing: null,
            Checks: DeploymentText.CheckNames.Select(n => new PreflightCheck(Name: n, Result: DeploymentText.Pass, Message: "")).ToList(),
            Impact: ServiceImpact(options, distributes), CreatedId: null);
    }

    // "a user" and "a UTC time" are correct, so U opens no vowel sound after "a".
    [GeneratedRegex(@"\b[Aa] (?=[AEIOaeio])|\b[Aa]n (?=[^AEIOUaeiou\s])")]
    private static partial Regex WrongArticle();

    [Fact]
    public void EveryOptionCombinationStatesEachFactOnceWithPlainLabelsAndCorrectArticles()
    {
        var choices = new[] { DeploymentText.Notifications, DeploymentText.NetworkBehaviors, DeploymentText.SlowNetworkBehaviors, DeploymentText.TaskSequenceContents,
                DeploymentText.TaskSequenceAvailabilities, DeploymentText.RerunBehaviors }
            .SelectMany(l => l).ToList();
        var rawValues = choices.Select(c => c.Value).Where(v => !choices.Any(c => c.Text.Contains(v, StringComparison.OrdinalIgnoreCase))).Distinct().ToList();
        string[] propertyNames = ["OverrideServiceWindow", "RebootOutsideServiceWindow", "AllowMetered", "AllowFallback", "FallbackToDp", "PostRebootScan", "SuppressRestart", "AllowMicrosoftUpdate", "ShowProgress"];
        var count = 0;
        foreach (var (type, options, distributes, backdated) in Combinations())
        {
            count++;
            var create = Create(type, options, distributes);
            var required = options.Purpose == DeploymentText.Required;
            var lines = DeploymentText.CreateLines(create, backdated);
            var where = $"{type} {options}";
            var text = string.Join("\n", lines.Append(DeploymentText.SecondConfirmation(create, required, backdated)));

            var doubled = lines.GroupBy(l => l, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            Assert.True(doubled.Count == 0, $"{where}: doubled lines: {string.Join(" | ", doubled)}");
            var article = WrongArticle().Match(text);
            Assert.False(article.Success, $"{where}: article error at '{(article.Success ? text.Substring(article.Index, Math.Min(40, text.Length - article.Index)) : "")}'");
            foreach (var raw in rawValues.Concat(propertyNames))
                Assert.False(text.Contains(raw, StringComparison.Ordinal), $"{where}: the raw name {raw} is in the plan.");
            Assert.True(lines.Count(l => l.StartsWith("Required", StringComparison.Ordinal)) <= 1, $"{where}: more than one Required line.");
            if (required)
            {
                var deadline = DeploymentText.Time(options.Deadline!.Value);
                Assert.Equal(1, lines.Count(l => l.Contains(deadline, StringComparison.Ordinal)));
                Assert.Equal(1, lines.Count(l => l.Contains("install", StringComparison.OrdinalIgnoreCase) && l.Contains("maintenance window", StringComparison.OrdinalIgnoreCase)));
                Assert.Equal(1, lines.Count(l => l.Contains("restart", StringComparison.OrdinalIgnoreCase) && l.Contains("maintenance window", StringComparison.OrdinalIgnoreCase)
                    && !l.Contains("suppress", StringComparison.OrdinalIgnoreCase)));
            }
            Assert.Equal(1, lines.Count(l => l == DeploymentText.UndoNote));
        }
        Assert.Equal(4 * 2 * 2 * 2 * 2 * 512, count);
    }

    [Fact]
    public void TheOptionsLineUsesTheLabelsOfTheForm()
    {
        var options = new DeploymentOptions(Purpose: DeploymentText.Required, AvailableTime: new DateTime(2026, 10, 20, 10, 0, 0), Deadline: new DateTime(2026, 10, 22, 10, 0, 0),
            UtcBasis: false, UserNotification: "DisplayAll", OverrideServiceWindow: true, RebootOutsideServiceWindow: false, AllowMetered: true,
            NetworkBehavior: "", RerunBehavior: "", ShowProgress: false, AllowFallback: false, FallbackToDp: true, PostRebootScan: true, SuppressRestart: false, AllowMicrosoftUpdate: true);
        var line = DeploymentText.OptionsLine(DeploymentObjectType.SoftwareUpdateGroup, options);
        Assert.Contains("Allow download on a metered connection: yes", line);
        Assert.Contains("Allow fallback to the default site boundary group: yes", line);
        Assert.Contains("Download from Microsoft Update when no distribution point has the content: yes", line);
        Assert.Contains("Require a full scan after the restart: yes", line);
        Assert.Contains("Suppress restarts on servers and workstations: no", line);
    }
}
