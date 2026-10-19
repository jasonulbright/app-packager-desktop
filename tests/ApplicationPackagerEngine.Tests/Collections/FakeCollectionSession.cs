using System.Globalization;
using System.Text.Json;
using ApplicationPackagerEngine.Services.ConfigMgrSession;

namespace ApplicationPackagerEngine.Tests.Collections;

/// <summary>
/// A ConfigMgr site in memory that answers the two Collections scripts the way the scripts and
/// ConfigMgr do on the lab site: names of collections are unique in the site, a collection that
/// limits, includes or excludes another cannot be removed, the query rule cmdlets match a rule
/// name in any case, a stored query rule expression can differ from the one sent
/// (<see cref="StoreExpression"/>), and a new collection or saved query gets a new id.
/// </summary>
internal sealed class FakeCollectionSession : IConfigMgrSession
{
    internal sealed class Rule
    {
        public required string Kind { get; init; }
        public string Name { get; set; } = "";
        public string Expression { get; set; } = "";
        public string ResourceId { get; init; } = "";
        public string IncludedId { get; init; } = "";
    }

    internal sealed class Collection
    {
        public required string Id { get; init; }
        public required string Name { get; set; }
        public string LimitId { get; set; } = "SMS00001";
        public int RefreshType { get; set; } = 2;
        public string Schedule { get; set; } = "0001200000100038";
        public string Comment { get; set; } = "";
        public string Path { get; set; } = "";
        public int Windows { get; set; }
        public int Deployments { get; set; }
        public List<Rule> Rules { get; } = [];
        public List<(string Id, string Name, bool Client, bool Active, bool Direct)> Members { get; } = [];
    }

    internal sealed class Query
    {
        public required string Id { get; init; }
        public required string Name { get; set; }
        public required string Expression { get; set; }
        public string Target { get; set; } = "SMS_R_System";
        public string Comment { get; set; } = "";
        public string Limit { get; set; } = "";
    }

    internal sealed record Call(string Script, string Operation, Dictionary<string, object?> Parameters, bool Cancellable);

    public List<Collection> Collections { get; } = [];
    public List<Query> Queries { get; } = [];
    public Dictionary<string, string> Resources { get; } = new() { ["16777220"] = "DC01", ["16777221"] = "CLIENT01" };
    public List<Call> Calls { get; } = [];

    /// <summary>The number of query rules on a collection after each rule change, by collection id.</summary>
    public Dictionary<string, List<int>> QueryRuleCounts { get; } = [];

    /// <summary>Called before each script runs; a returned exception is thrown.</summary>
    public Func<Call, Exception?>? Throw { get; set; }

    /// <summary>Called after a write script made its change; a returned exception is thrown although the change stays made.</summary>
    public Func<Call, Exception?>? AfterWrite { get; set; }

    /// <summary>Called before each read; lets a test change the site between a plan and its write.</summary>
    public Action<Call>? BeforeRun { get; set; }

    /// <summary>A rule add (kind, name) or remove ("remove", name) for which ConfigMgr reports an error.</summary>
    public Func<string, string, bool>? RuleCmdletFails { get; set; }

    /// <summary>The expression ConfigMgr stores for the one sent.</summary>
    public Func<string, string> StoreExpression { get; set; } = e => e;

    /// <summary>Expressions the SMS Provider rejects.</summary>
    public Func<string, bool> ProviderRejects { get; set; } = e => e.Contains("xx", StringComparison.Ordinal);

    public List<List<string>> PreviewRows { get; set; } = [["CM01", "16777219"], ["DC01", "16777220"], ["CLIENT01", "16777221"]];

    public IEnumerable<Call> Writes => Calls.Where(c => c.Script == "WriteCollection");

    private int _nextId = 0x50;
    private int _nextQuery = 1;

    public Collection Add(string id, string name, string limit = "SMS00001", params Rule[] rules)
    {
        var c = new Collection { Id = id, Name = name, LimitId = limit };
        c.Rules.AddRange(rules);
        Collections.Add(c);
        return c;
    }

    public static Rule QueryRule(string name, string expression) => new() { Kind = "Query", Name = name, Expression = expression };
    public static Rule DirectRule(string resourceId, string name) => new() { Kind = "Direct", Name = name, ResourceId = resourceId };
    public static Rule IncludeRule(string id, string name) => new() { Kind = "Include", Name = name, IncludedId = id };
    public static Rule ExcludeRule(string id, string name) => new() { Kind = "Exclude", Name = name, IncludedId = id };

    public Collection? Find(string id) => Collections.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));

    public Task<ConfigMgrCommandResult> InvokeAsync(ConfigMgrScript script, ConfigMgrParameters? parameters = null, CancellationToken ct = default, TimeSpan? timeout = null)
    {
        ct.ThrowIfCancellationRequested();
        var values = (parameters ?? new ConfigMgrParameters()).ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        var call = new Call(script.Name, S(values.GetValueOrDefault("Operation")), values, ct.CanBeCanceled);
        lock (Calls) Calls.Add(call);
        BeforeRun?.Invoke(call);
        if (Throw?.Invoke(call) is { } ex) return Task.FromException<ConfigMgrCommandResult>(ex);
        var output = script.Name switch
        {
            "ReadCollections" => Read(call.Operation, values),
            "WriteCollection" => [Write(call.Operation, values)],
            _ => throw new InvalidOperationException("Unknown script " + script.Name)
        };
        if (script.Name == "WriteCollection" && AfterWrite?.Invoke(call) is { } after)
            return Task.FromException<ConfigMgrCommandResult>(after);
        return Task.FromResult(new ConfigMgrCommandResult(output.Select(o => JsonSerializer.SerializeToElement(o)).ToList(), []));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static string S(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";

    private static string[] A(object? value) => value as string[] ?? [];

    private static object Row(Collection c) => new
    {
        Kind = "Collection", CollectionId = c.Id, c.Name, ObjectPath = c.Path, LimitToCollectionId = c.LimitId,
        LimitToCollectionName = c.LimitId.Length == 0 ? "" : "Limit " + c.LimitId, MemberCount = c.Members.Count, c.RefreshType,
        RefreshSchedule = c.Schedule, c.Comment, WindowCount = c.Windows,
        DirectRuleCount = c.Rules.Count(r => r.Kind == "Direct"), QueryRuleCount = c.Rules.Count(r => r.Kind == "Query"),
        IncludeRuleCount = c.Rules.Count(r => r.Kind == "Include"), ExcludeRuleCount = c.Rules.Count(r => r.Kind == "Exclude")
    };

    private static object RuleRow(Rule r) => new
    {
        Kind = "Rule", RuleKind = r.Kind, RuleName = r.Name, Expression = r.Expression, ResourceId = r.ResourceId, IncludedCollectionId = r.IncludedId
    };

    private static object QueryRow(Query q) => new
    {
        Kind = "SavedQuery", QueryId = q.Id, q.Name, q.Expression, TargetClassName = q.Target, q.Comment, LimitToCollectionId = q.Limit
    };

    private IEnumerable<(string Id, string Relation)> Dependents(string id)
    {
        foreach (var c in Collections)
        {
            if (string.Equals(c.LimitId, id, StringComparison.OrdinalIgnoreCase)) yield return (c.Id, "Limiting");
            foreach (var r in c.Rules.Where(r => r.Kind is "Include" or "Exclude" && string.Equals(r.IncludedId, id, StringComparison.OrdinalIgnoreCase)))
                yield return (c.Id, r.Kind);
        }
    }

    private List<object> Read(string operation, Dictionary<string, object?> p)
    {
        var rows = new List<object>();
        var id = S(p.GetValueOrDefault("CollectionId"));
        switch (operation)
        {
            case "List":
                rows.AddRange(Collections.Select(Row));
                break;
            case "Get":
            case "Detail":
            case "Impact":
                if (Find(id) is not { } c) break;
                rows.Add(Row(c));
                rows.AddRange(c.Rules.Select(RuleRow));
                if (operation == "Detail")
                {
                    var size = Convert.ToInt32(p["PageSize"], CultureInfo.InvariantCulture);
                    var page = Convert.ToInt32(p["MemberPage"], CultureInfo.InvariantCulture);
                    var members = c.Members.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
                    rows.Add(new { Kind = "MemberTotal", Count = members.Count });
                    rows.AddRange(members.Skip(page * size).Take(size).Select(m => (object)new
                    {
                        Kind = "Member", ResourceId = m.Id, m.Name, IsClient = m.Client, IsActive = m.Active, IsDirect = m.Direct
                    }));
                }
                if (operation == "Impact")
                {
                    rows.Add(new { Kind = "Impact", c.Deployments });
                    rows.AddRange(Dependents(c.Id).Select(d => (object)new { Kind = "Reference", CollectionId = d.Id, Find(d.Id)!.Name, d.Relation }));
                }
                break;
            case "FindCollection":
                rows.AddRange(Collections.Where(c => string.Equals(c.Name, S(p["Name"]), StringComparison.OrdinalIgnoreCase))
                    .Select(c => (object)new { Kind = "Found", CollectionId = c.Id, c.Name, CollectionType = 2 }));
                break;
            case "Resource":
                var resource = S(p["ResourceId"]);
                if (Resources.TryGetValue(resource, out var name)) rows.Add(new { Kind = "Resource", ResourceId = resource, Name = name });
                break;
            case "Validate":
            case "Preview":
                var expression = S(p["Expression"]);
                if (ProviderRejects(expression))
                {
                    rows.Add(new { Kind = "Wql", Valid = false, Count = 0, Columns = Array.Empty<string>(), Detail = "System.Management.ManagementException: Invalid class at C:\\secret\\path" });
                    break;
                }
                var max = operation == "Preview" ? Convert.ToInt32(p["MaxRows"], CultureInfo.InvariantCulture) : 0;
                rows.Add(new { Kind = "Wql", Valid = true, Count = PreviewRows.Count, Columns = operation == "Preview" ? new[] { "Name", "ResourceId" } : [], Detail = "" });
                rows.AddRange(PreviewRows.Take(max).Select(r => (object)new { Kind = "WqlRow", Values = r.ToArray() }));
                break;
            case "SavedQueries":
                rows.AddRange(Queries.Select(QueryRow));
                break;
            case "Query":
                rows.AddRange(Queries.Where(q => q.Id == S(p["QueryId"])).Select(QueryRow));
                break;
            case "FindQuery":
                rows.AddRange(Queries.Where(q => string.Equals(q.Name, S(p["Name"]), StringComparison.OrdinalIgnoreCase)).Select(QueryRow));
                break;
            default:
                throw new InvalidOperationException(operation);
        }
        return rows;
    }

    private object Result(string status, Collection? c = null, string detail = "", Query? q = null, string[]? references = null, List<string>? steps = null) => new
    {
        Kind = "Result", Status = status, Detail = detail, Steps = (steps ?? []).ToArray(),
        Collection = c is null ? null : Row(c), Rules = c is null ? [] : c.Rules.Select(RuleRow).ToArray(),
        Query = q is null ? null : QueryRow(q), References = references ?? []
    };

    private static string Key(Rule r) => r.Kind switch
    {
        "Direct" => "Direct|" + r.ResourceId,
        "Query" => "Query|" + r.Name + "|" + r.Expression,
        _ => r.Kind + "|" + r.IncludedId.ToUpperInvariant()
    };

    private void Track(Collection c)
    {
        if (!QueryRuleCounts.TryGetValue(c.Id, out var list)) QueryRuleCounts[c.Id] = list = [];
        list.Add(c.Rules.Count(r => r.Kind == "Query"));
    }

    private Rule? FindRule(Collection c, string kind, string name, string resource, string included) => c.Rules.FirstOrDefault(r => r.Kind == kind && kind switch
    {
        "Direct" => r.ResourceId == resource,
        "Query" => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase),
        _ => string.Equals(r.IncludedId, included, StringComparison.OrdinalIgnoreCase)
    });

    private void AddRule(Collection c, string kind, string name, string expression, string resource, string included)
    {
        if (RuleCmdletFails?.Invoke(kind, name) == true || (kind == "Query" && ProviderRejects(expression)))
            throw new InvalidOperationException("The SMS Provider reported an error.");
        c.Rules.Add(kind switch
        {
            "Query" => QueryRule(name, StoreExpression(expression)),
            "Direct" => DirectRule(resource, Resources.GetValueOrDefault(resource, resource)),
            "Include" => IncludeRule(included, Find(included)!.Name),
            _ => ExcludeRule(included, Find(included)!.Name)
        });
        Track(c);
    }

    private void RemoveRule(Collection c, string kind, string name, string resource, string included)
    {
        if (RuleCmdletFails?.Invoke("remove", name) == true) throw new InvalidOperationException("The SMS Provider reported an error.");
        c.Rules.RemoveAll(r => r.Kind == kind && kind switch
        {
            "Direct" => r.ResourceId == resource,
            "Query" => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase),
            _ => string.Equals(r.IncludedId, included, StringComparison.OrdinalIgnoreCase)
        });
        Track(c);
    }

    private object Write(string operation, Dictionary<string, object?> p)
    {
        if (operation is "SaveQuery" or "UpdateQuery" or "DeleteQuery") return WriteQuery(operation, p);
        var id = S(p.GetValueOrDefault("CollectionId"));
        var name = S(p.GetValueOrDefault("Name"));
        Collection? c = null;
        if (operation != "Create")
        {
            c = Find(id);
            if (c is null) return Result("collection-missing");
            if (operation is not ("Copy" or "Evaluate") && id.StartsWith("SMS", StringComparison.OrdinalIgnoreCase)) return Result("built-in", c);
        }
        if (p.GetValueOrDefault("CheckState") is true && c is not null)
        {
            var changed = c.Name != S(p["ExpectedName"]);
            if (operation == "SetProperties")
                changed |= c.Comment != S(p["ExpectedComment"]) || !string.Equals(c.LimitId, S(p["ExpectedLimitingId"]), StringComparison.OrdinalIgnoreCase)
                    || c.RefreshType != (int)p["ExpectedRefreshType"]! || c.Schedule != S(p["ExpectedRefreshSchedule"]);
            if (operation == "Remove")
                changed |= !c.Rules.Select(Key).Order(StringComparer.Ordinal).SequenceEqual(A(p["ExpectedRuleKeys"]).Order(StringComparer.Ordinal));
            if (changed) return Result("changed", c);
        }
        var kind = S(p.GetValueOrDefault("RuleKind"));
        var ruleName = S(p.GetValueOrDefault("RuleName"));
        var resource = p.TryGetValue("ResourceId", out var res) ? S(res) : "0";
        var included = S(p.GetValueOrDefault("IncludedCollectionId"));
        var oldName = S(p.GetValueOrDefault("OldRuleName"));
        switch (operation)
        {
            case "Create":
                if (Collections.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))) return Result("name-taken");
                if (Find(S(p["LimitingCollectionId"])) is null) return Result("limiting-missing");
                break;
            case "Copy":
                if (Collections.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))) return Result("name-taken", c);
                break;
            case "SetProperties":
                if (!string.Equals(name, c!.Name, StringComparison.OrdinalIgnoreCase) && Collections.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))) return Result("name-taken", c);
                if (string.Equals(S(p["LimitingCollectionId"]), id, StringComparison.OrdinalIgnoreCase)) return Result("limiting-self", c);
                if (Find(S(p["LimitingCollectionId"])) is null) return Result("limiting-missing", c);
                break;
            case "AddRule":
                if (FindRule(c!, kind, ruleName, resource, included) is not null) return Result("rule-taken", c);
                if (kind == "Direct" && !Resources.ContainsKey(resource)) return Result("target-missing", c);
                if (kind is "Include" or "Exclude" && (Find(included) is null || string.Equals(included, id, StringComparison.OrdinalIgnoreCase))) return Result("target-missing", c);
                break;
            case "RemoveRule":
            case "UpdateRule":
            {
                var rule = FindRule(c!, operation == "UpdateRule" ? "Query" : kind, oldName, resource, included);
                if (rule is null) return Result("rule-missing", c);
                if (rule.Kind == "Query" && rule.Expression != S(p["OldExpression"])) return Result("rule-changed", c);
                if (rule.Kind == "Query" && c!.Rules.Count(r => r.Kind == "Query" && string.Equals(r.Name, oldName, StringComparison.OrdinalIgnoreCase)) > 1) return Result("rule-duplicate", c);
                if (operation == "UpdateRule")
                {
                    var same = string.Equals(ruleName, oldName, StringComparison.OrdinalIgnoreCase);
                    var check = same ? S(p["TempRuleName"]) : ruleName;
                    if (FindRule(c!, "Query", check, "", "") is not null) return Result("rule-taken", c);
                }
                break;
            }
            case "Remove":
            {
                var deps = Dependents(id).ToList();
                if (deps.Any(d => d.Relation == "Limiting")) return Result("is-limiting", c, references: deps.Where(d => d.Relation == "Limiting").Select(d => d.Id).ToArray());
                if (deps.Count > 0) return Result("is-referenced", c, references: deps.Select(d => d.Id).ToArray());
                break;
            }
        }

        var steps = new List<string>();
        var mutated = false;
        try
        {
            switch (operation)
            {
                case "Create":
                {
                    mutated = true;
                    var made = new Collection
                    {
                        Id = "MCM000" + (_nextId++).ToString("X2", CultureInfo.InvariantCulture), Name = name, LimitId = S(p["LimitingCollectionId"]),
                        RefreshType = CollectionRefresh(S(p["RefreshType"])), Comment = S(p["Comment"]),
                        Schedule = S(p["RefreshType"]) is "Periodic" or "Both" ? (S(p["RefreshSchedule"]) is { Length: 16 } s ? s : "0001200000100038") : "0001200000100008"
                    };
                    Collections.Add(made);
                    steps.Add("created");
                    var kinds = A(p["RuleKinds"]);
                    var failed = new List<string>();
                    for (var i = 0; i < kinds.Length; i++)
                    {
                        try { AddRule(made, kinds[i], A(p["RuleNames"])[i], A(p["RuleExpressions"])[i], A(p["RuleResourceIds"])[i], A(p["RuleCollectionIds"])[i]); }
                        catch (InvalidOperationException) { failed.Add(i.ToString(CultureInfo.InvariantCulture)); }
                    }
                    return failed.Count > 0 ? Result("rules-partial", made, string.Join(",", failed)) : Result("ok", made);
                }
                case "Copy":
                {
                    mutated = true;
                    var copy = new Collection
                    {
                        Id = "MCM000" + (_nextId++).ToString("X2", CultureInfo.InvariantCulture), Name = name, LimitId = c!.LimitId,
                        RefreshType = c.RefreshType, Schedule = c.Schedule, Comment = c.Comment
                    };
                    copy.Rules.AddRange(c.Rules.Select(r => new Rule { Kind = r.Kind, Name = r.Name, Expression = r.Expression, ResourceId = r.ResourceId, IncludedId = r.IncludedId }));
                    Collections.Add(copy);
                    return Result("ok", copy);
                }
                case "SetProperties":
                    mutated = true;
                    c!.Name = name;
                    c.Comment = S(p["Comment"]);
                    c.LimitId = S(p["LimitingCollectionId"]);
                    c.RefreshType = CollectionRefresh(S(p["RefreshType"]));
                    if (S(p["RefreshSchedule"]) is { Length: 16 } schedule) c.Schedule = schedule;
                    return Result("ok", c);
                case "AddRule":
                    mutated = true;
                    AddRule(c!, kind, ruleName, S(p.GetValueOrDefault("Expression")), resource, included);
                    return Result("ok", c);
                case "RemoveRule":
                    mutated = true;
                    RemoveRule(c!, kind, oldName, resource, included);
                    return Result("ok", c);
                case "UpdateRule":
                {
                    var expression = S(p["Expression"]);
                    var temp = S(p["TempRuleName"]);
                    try
                    {
                        if (string.Equals(ruleName, oldName, StringComparison.OrdinalIgnoreCase))
                        {
                            steps.Add("add-temporary"); AddRule(c!, "Query", temp, expression, "", "");
                            steps.Add("remove-old"); RemoveRule(c!, "Query", oldName, "", "");
                            steps.Add("add-final"); AddRule(c!, "Query", ruleName, expression, "", "");
                            steps.Add("remove-temporary"); RemoveRule(c!, "Query", temp, "", "");
                        }
                        else
                        {
                            steps.Add("add-final"); AddRule(c!, "Query", ruleName, expression, "", "");
                            steps.Add("remove-old"); RemoveRule(c!, "Query", oldName, "", "");
                        }
                        steps.Add("done");
                    }
                    catch (InvalidOperationException ex)
                    {
                        return Result("update-failed", c, ex.Message, steps: steps);
                    }
                    return Result("ok", c, steps: steps);
                }
                case "Evaluate":
                    mutated = true;
                    return Result("ok", c);
                case "Remove":
                    mutated = true;
                    Collections.Remove(c!);
                    return Result("ok");
                default:
                    throw new InvalidOperationException(operation);
            }
        }
        catch (InvalidOperationException ex) when (mutated)
        {
            return Result("partial", operation == "Create" ? Collections.FirstOrDefault(x => x.Name == name) : Find(id), ex.Message);
        }
    }

    private static int CollectionRefresh(string type) => type switch { "Manual" => 1, "Periodic" => 2, "Continuous" => 4, "Both" => 6, _ => 0 };

    private object WriteQuery(string operation, Dictionary<string, object?> p)
    {
        var id = S(p.GetValueOrDefault("QueryId"));
        var name = S(p.GetValueOrDefault("Name"));
        Query? current = null;
        if (operation != "SaveQuery")
        {
            current = Queries.FirstOrDefault(q => q.Id == id);
            if (current is null) return Result("query-missing");
            if (id.StartsWith("SMS", StringComparison.OrdinalIgnoreCase)) return Result("query-built-in", q: current);
            if (current.Name != S(p["ExpectedName"]) || current.Expression != S(p["ExpectedExpression"]) || current.Comment != S(p["ExpectedComment"])
                || current.Target != S(p["ExpectedTargetClassName"]) || current.Limit != S(p["ExpectedLimitToCollectionId"]))
                return Result("query-changed", q: current);
        }
        if (operation != "DeleteQuery" && (current is null || !string.Equals(name, current.Name, StringComparison.OrdinalIgnoreCase))
            && Queries.FirstOrDefault(q => string.Equals(q.Name, name, StringComparison.OrdinalIgnoreCase)) is { } taken)
            return Result("query-name-taken", q: taken);
        switch (operation)
        {
            case "SaveQuery":
                var made = new Query
                {
                    Id = "MCM" + (_nextQuery++).ToString("D5", CultureInfo.InvariantCulture), Name = name, Expression = S(p["Expression"]),
                    Comment = S(p["Comment"]), Target = S(p["TargetClassName"]), Limit = S(p["LimitToCollectionId"])
                };
                Queries.Add(made);
                return Result("ok", q: made);
            case "UpdateQuery":
                current!.Name = name;
                current.Expression = S(p["Expression"]);
                current.Comment = S(p["Comment"]);
                current.Target = S(p["TargetClassName"]);
                current.Limit = S(p["LimitToCollectionId"]);
                return Result("ok", q: current);
            default:
                Queries.Remove(current!);
                return Result("ok", q: current);
        }
    }
}
