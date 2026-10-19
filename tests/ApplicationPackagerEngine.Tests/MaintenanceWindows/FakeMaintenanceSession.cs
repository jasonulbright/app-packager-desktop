using System.Globalization;
using System.Text.Json;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.ConfigMgrSession;

namespace ApplicationPackagerEngine.Tests.MaintenanceWindows;

/// <summary>
/// A ConfigMgr site in memory that answers the two Maintenance Windows scripts the way the
/// scripts and ConfigMgr do: names are unique without regard to case, a new window gets a
/// new id, and New-CMSchedule parameters become a schedule token.
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
        public string Description { get; set; } = "ConfigMgr text";
    }

    internal sealed record Call(string Script, string Text, Dictionary<string, object?> Parameters, bool Cancellable);

    public List<Collection> Collections { get; } = [];
    public List<Window> Windows { get; } = [];
    public List<Call> Calls { get; } = [];

    /// <summary>Called before each script runs; a returned exception is thrown, a returned status is answered instead of the write.</summary>
    public Func<Call, Exception?>? Throw { get; set; }
    public Func<Call, (string Status, string Detail)?>? Answer { get; set; }

    /// <summary>Changes the token ConfigMgr stores, as a clamp would.</summary>
    public Func<string, string>? Store { get; set; }

    public IEnumerable<Call> Writes => Calls.Where(c => c.Script == "WriteMaintenanceWindow");

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
        return Task.FromResult(new ConfigMgrCommandResult(output.Select(o => JsonSerializer.SerializeToElement(o)).ToList(), []));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public Window Add(string collectionId, string name, string token, int type = 1, bool enabled = true)
    {
        var window = new Window { CollectionId = collectionId, Name = name, Schedules = token, Type = type, Enabled = enabled };
        Windows.Add(window);
        return window;
    }

    private static string S(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";

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
        return rows;
    }

    private static object Row(Window w) => new
    {
        Kind = "Window", w.CollectionId, w.Name, ServiceWindowId = w.Id, w.Type, w.Enabled, w.Schedules, w.Description
    };

    private object Result(string status, Window? window = null, string detail = "") =>
        new { Kind = "Result", Status = status, Detail = detail, Window = window is null ? null : Row(window) };

    private object Write(Call call, Dictionary<string, object?> p)
    {
        var collectionId = S(p["CollectionId"]);
        if (!Collections.Any(c => c.Id == collectionId)) return Result("collection-missing");
        var operation = S(p["Operation"]);
        var windows = Windows.Where(w => w.CollectionId == collectionId).ToList();
        Window? current = null;
        if (operation == "Create")
        {
            var taken = windows.FirstOrDefault(w => string.Equals(w.Name, S(p["Name"]), StringComparison.OrdinalIgnoreCase));
            if (taken is not null) return Result("name-taken", taken);
        }
        else
        {
            current = windows.FirstOrDefault(w => w.Id == S(p["ServiceWindowId"]));
            if (current is null) return Result("window-missing");
            var expected = ((string[]?)p.GetValueOrDefault("ExpectedSchedules")) ?? [];
            if (current.Name != S(p["ExpectedName"]) || current.Type != (int)p["ExpectedType"]! || current.Enabled != (bool)p["ExpectedEnabled"]!
                || (expected.Length > 0 && !expected.Contains(current.Schedules)))
                return Result("changed", current);
        }

        if (Answer?.Invoke(call) is { } answer) return Result(answer.Status, null, answer.Detail);

        switch (operation)
        {
            case "Create":
                var created = new Window
                {
                    CollectionId = collectionId, Name = S(p["Name"]), Type = TypeOf(p), Enabled = (bool)p["IsEnabled"]!, Schedules = Token(p)
                };
                Windows.Add(created);
                return Result("ok", created);
            case "Update":
                current!.Schedules = Token(p);
                current.Type = TypeOf(p);
                current.Enabled = (bool)p["IsEnabled"]!;
                return Result("ok", current);
            case "SetEnabled":
                current!.Enabled = (bool)p["IsEnabled"]!;
                return Result("ok", current);
            case "Remove":
                Windows.Remove(current!);
                return Result("ok", current);
            default:
                throw new InvalidOperationException(operation);
        }
    }

    private static int TypeOf(Dictionary<string, object?> p) => S(p["ApplyTo"]) switch
    {
        "SoftwareUpdatesOnly" => 4,
        "TaskSequencesOnly" => 5,
        _ => 1
    };

    /// <summary>The token New-CMSchedule and the duration fields make from the script parameters.</summary>
    private string Token(Dictionary<string, object?> p)
    {
        var start = DateTime.ParseExact(S(p["Start"]), "yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture);
        var duration = TimeSpan.FromMinutes((int)p["DurationHours"]! * 60 + (int)p["DurationMinutes"]!);
        var utc = (bool)p["IsUtc"]!;
        var count = (int)p["RecurCount"]!;
        DayOfWeek Day() => Enum.Parse<DayOfWeek>(S(p["DayOfWeek"]));
        int Week() => S(p["WeekOrder"]) switch { "First" => 1, "Second" => 2, "Third" => 3, "Fourth" => 4, _ => 5 };
        var offset = p.TryGetValue("OffsetDay", out var o) ? (int)o! : 0;
        var schedule = S(p["Recurrence"]) switch
        {
            "None" => new MaintenanceSchedule(MaintenanceScheduleKind.OneTime, start, duration, utc, 1, null, null, null, 0, null),
            "Days" => new MaintenanceSchedule(MaintenanceScheduleKind.Daily, start, duration, utc, count, null, null, null, 0, null),
            "Weekly" => new MaintenanceSchedule(MaintenanceScheduleKind.Weekly, start, duration, utc, count, Day(), null, null, 0, null),
            "MonthlyByDate" => new MaintenanceSchedule(MaintenanceScheduleKind.MonthlyByDate, start, duration, utc, count, null, (int)p["DayOfMonth"]!, null, 0, null),
            "MonthlyByWeekday" => new MaintenanceSchedule(MaintenanceScheduleKind.MonthlyByWeekday, start, duration, utc, count, Day(), null, Week(), offset, null),
            var other => throw new InvalidOperationException(other)
        };
        var token = ScheduleToken.Encode(schedule);
        return Store?.Invoke(token) ?? token;
    }
}
