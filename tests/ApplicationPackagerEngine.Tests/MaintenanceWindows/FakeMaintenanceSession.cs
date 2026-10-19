using System.Globalization;
using System.Text.Json;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.ConfigMgrSession;

namespace ApplicationPackagerEngine.Tests.MaintenanceWindows;

/// <summary>
/// A ConfigMgr site in memory that answers the two Maintenance Windows scripts the way the
/// scripts and ConfigMgr do: New-CMMaintenanceWindow refuses a name in any case, but windows
/// written through the collection settings can share a name; Remove-CMMaintenanceWindow
/// removes every window with the name; a create or a schedule update replaces the
/// description with ConfigMgr's schedule text; a new window gets a new id; and New-CMSchedule
/// parameters become a schedule token through an encoder of this file, not the product's.
/// </summary>
internal sealed class FakeMaintenanceSession : IConfigMgrSession
{
    internal sealed class Collection(string id, string name, string path = "", int members = 0)
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        public string Path { get; } = path;
        public int Members { get; } = members;
    }

    internal sealed class Window
    {
        public required string CollectionId { get; set; }
        public required string Name { get; set; }
        public string Id { get; set; } = "{" + Guid.NewGuid().ToString().ToUpperInvariant() + "}";
        public int Type { get; set; } = 1;
        public bool Enabled { get; set; } = true;
        public required string Schedules { get; set; }
        public string Description { get; set; } = "";
    }

    internal sealed record Call(string Script, string Text, Dictionary<string, object?> Parameters, bool Cancellable);

    public List<Collection> Collections { get; } = [];
    public List<Window> Windows { get; } = [];
    public List<Call> Calls { get; } = [];

    /// <summary>Called before each script runs; a returned exception is thrown, a returned status is answered instead of the write.</summary>
    public Func<Call, Exception?>? Throw { get; set; }
    public Func<Call, (string Status, string Detail)?>? Answer { get; set; }

    /// <summary>Called after a write script made its change; a returned exception is thrown although the change stays made.</summary>
    public Func<Call, Exception?>? AfterWrite { get; set; }

    /// <summary>Changes the token ConfigMgr stores, as a clamp would.</summary>
    public Func<string, string>? Store { get; set; }

    /// <summary>False models a ConfigMgr module without Get-CMCollectionSetting: no description write and no removal by id.</summary>
    public bool DescriptionWritable { get; set; } = true;

    /// <summary>True makes a description write through the collection settings fail.</summary>
    public bool DescriptionWriteFails { get; set; }

    /// <summary>The windows that left the site through Remove-CMMaintenanceWindow, by name.</summary>
    public List<string> RemovedByName { get; } = [];

    public IEnumerable<Call> Writes => Calls.Where(c => c.Script == "WriteMaintenanceWindow" && S(c.Parameters["Operation"]) != "Check");

    public Task<ConfigMgrCommandResult> InvokeAsync(ConfigMgrScript script, ConfigMgrParameters? parameters = null, CancellationToken ct = default, TimeSpan? timeout = null)
    {
        ct.ThrowIfCancellationRequested();
        var values = (parameters ?? new ConfigMgrParameters()).ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        var call = new Call(script.Name, script.Text, values, ct.CanBeCanceled);
        lock (Calls) Calls.Add(call);
        if (Throw?.Invoke(call) is { } ex) return Task.FromException<ConfigMgrCommandResult>(ex);
        var output = script.Name switch
        {
            "ListMaintenanceWindows" => List(values),
            "WriteMaintenanceWindow" => [Write(call, values)],
            _ => throw new InvalidOperationException("Unknown script " + script.Name)
        };
        if (script.Name == "WriteMaintenanceWindow" && S(values["Operation"]) != "Check" && AfterWrite?.Invoke(call) is { } after)
            return Task.FromException<ConfigMgrCommandResult>(after);
        return Task.FromResult(new ConfigMgrCommandResult(output.Select(o => JsonSerializer.SerializeToElement(o)).ToList(), []));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>Adds a window as the collection settings would store it: no name check; the description defaults to ConfigMgr's text.</summary>
    public Window Add(string collectionId, string name, string token, int type = 1, bool enabled = true, string? description = null)
    {
        var window = new Window
        {
            CollectionId = collectionId, Name = name, Schedules = token, Type = type, Enabled = enabled,
            Description = description ?? ConfigMgrText(ScheduleToken.Decode(token))
        };
        Windows.Add(window);
        return window;
    }

    private static string S(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";

    private static string ConfigMgrText(MaintenanceSchedule? schedule) =>
        schedule is null ? "Occurs every 8 hours" : new ScheduleProjector(TimeZoneInfo.Utc).Describe(schedule);

    private List<object> List(Dictionary<string, object?> p)
    {
        var id = S(p.GetValueOrDefault("CollectionId"));
        var collectionsOnly = p.GetValueOrDefault("CollectionsOnly") is true;
        var collections = Collections.Where(c => id.Length == 0 || string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase)).ToList();
        var rows = new List<object>();
        foreach (var c in collections)
            rows.Add(new { Kind = "Collection", CollectionId = c.Id, c.Name, FolderPath = c.Path, MemberCount = c.Members, WindowCount = Windows.Count(w => w.CollectionId == c.Id) });
        if (!collectionsOnly)
            foreach (var c in collections)
                foreach (var w in Windows.Where(w => w.CollectionId == c.Id))
                    rows.Add(Row(w));
        if (id.Length > 0)
            rows.Add(new { Kind = "Capability", DescriptionWritable });
        return rows;
    }

    private static object Row(Window w) => new
    {
        Kind = "Window", w.CollectionId, w.Name, ServiceWindowId = w.Id, w.Type, w.Enabled, w.Schedules, w.Description
    };

    private static object Result(string status, Window? window = null, string detail = "") =>
        new { Kind = "Result", Status = status, Detail = detail, Window = window is null ? null : Row(window) };

    private static bool SameName(Window w, string name) => string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase);

    private object Write(Call call, Dictionary<string, object?> p)
    {
        var collectionId = S(p["CollectionId"]);
        if (!Collections.Any(c => c.Id == collectionId)) return Result("collection-missing");
        var operation = S(p["Operation"]);
        var windows = Windows.Where(w => w.CollectionId == collectionId).ToList();
        Window? current = null;
        if (operation == "Create")
        {
            var taken = windows.FirstOrDefault(w => SameName(w, S(p["Name"])));
            if (taken is not null) return Result("name-taken", taken);
        }
        else
        {
            current = windows.FirstOrDefault(w => w.Id == S(p["ServiceWindowId"]));
            if (current is null) return Result("window-missing");
            var expected = ((string[]?)p.GetValueOrDefault("ExpectedSchedules")) ?? [];
            if (current.Name != S(p["ExpectedName"]) || current.Type != (int)p["ExpectedType"]! || current.Enabled != (bool)p["ExpectedEnabled"]!
                || (expected.Length > 0 && !expected.Contains(current.Schedules))
                || (p.TryGetValue("ExpectedDescription", out var expectedDescription) && current.Description != S(expectedDescription)))
                return Result("changed", current);
        }
        if (p.GetValueOrDefault("RequireDescription") is true && !DescriptionWritable)
            return Result("description-unsupported", current);
        if (operation == "Check") return Result("ok", current);

        if (Answer?.Invoke(call) is { } answer) return Result(answer.Status, null, answer.Detail);

        var description = S(p.GetValueOrDefault("Description"));
        switch (operation)
        {
            case "Create":
                var (token, text) = Token(p);
                var created = new Window
                {
                    CollectionId = collectionId, Name = S(p["Name"]), Type = TypeOf(p), Enabled = (bool)p["IsEnabled"]!, Schedules = token, Description = text
                };
                Windows.Add(created);
                return WithDescription(created, description);
            case "Update":
                (current!.Schedules, current.Description) = Token(p);
                current.Type = TypeOf(p);
                current.Enabled = (bool)p["IsEnabled"]!;
                return WithDescription(current, description);
            case "SetEnabled":
                current!.Enabled = (bool)p["IsEnabled"]!;
                return Result("ok", current);
            case "Remove":
                var sameName = windows.Where(w => SameName(w, current!.Name)).ToList();
                if (sameName.Count > 1)
                {
                    if (!DescriptionWritable) return Result("duplicate-name", current);
                    Windows.Remove(current!);
                }
                else
                {
                    foreach (var w in Windows.Where(w => w.CollectionId == collectionId && SameName(w, current!.Name)).ToList())
                    {
                        Windows.Remove(w);
                        RemovedByName.Add(w.Name);
                    }
                }
                return Result("ok", current);
            default:
                throw new InvalidOperationException(operation);
        }
    }

    private object WithDescription(Window window, string description)
    {
        if (description.Length == 0 || window.Description == description) return Result("ok", window);
        if (!DescriptionWritable || DescriptionWriteFails) return Result("description-failed", window, "The description could not be set.");
        window.Description = description;
        return Result("ok", window);
    }

    private static int TypeOf(Dictionary<string, object?> p) => S(p["ApplyTo"]) switch
    {
        "SoftwareUpdatesOnly" => 4,
        "TaskSequencesOnly" => 5,
        _ => 1
    };

    /// <summary>The token New-CMSchedule and the duration fields make from the script parameters, and ConfigMgr's text for it.</summary>
    private (string Token, string Text) Token(Dictionary<string, object?> p)
    {
        var start = DateTime.ParseExact(S(p["Start"]), "yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture);
        var minutes = (int)p["DurationHours"]! * 60 + (int)p["DurationMinutes"]!;
        var utc = (bool)p["IsUtc"]!;
        var count = (int)p["RecurCount"]!;
        int Day() => (int)Enum.Parse<DayOfWeek>(S(p["DayOfWeek"])) + 1;
        int Week() => S(p["WeekOrder"]) switch { "First" => 1, "Second" => 2, "Third" => 3, "Fourth" => 4, _ => 0 };
        var offset = p.TryGetValue("OffsetDay", out var o) ? (int)o! : 0;

        ulong v = ((ulong)start.Minute << 58) | ((ulong)start.Hour << 53) | ((ulong)start.Day << 48) | ((ulong)start.Month << 44)
            | ((ulong)(start.Year - 1970) << 38) | ((ulong)(minutes % 60) << 32) | ((ulong)(minutes / 60) << 27) | (utc ? 1UL : 0UL);
        v |= S(p["Recurrence"]) switch
        {
            "None" => 1UL << 19,
            "Days" => (2UL << 19) | ((ulong)count << 3),
            "Weekly" => (3UL << 19) | ((ulong)Day() << 16) | ((ulong)count << 13),
            "MonthlyByDate" => (5UL << 19) | ((ulong)(int)p["DayOfMonth"]! << 14) | ((ulong)count << 10),
            "MonthlyByWeekday" => ((offset > 0 ? 6UL : 4UL) << 19) | ((ulong)Day() << 16) | ((ulong)count << 12) | ((ulong)Week() << 9) | ((ulong)offset << 6),
            var other => throw new InvalidOperationException(other)
        };
        var token = v.ToString("X16", CultureInfo.InvariantCulture);
        token = Store?.Invoke(token) ?? token;
        return (token, ConfigMgrText(ScheduleToken.Decode(token)));
    }
}
