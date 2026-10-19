using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Services.ConfigMgrSession;
using ApplicationPackagerEngine.Services.Logging;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Reads and changes device collections, their rules and the saved queries of the site
/// through the ConfigMgr session. Each change is one script run that checks the state the
/// change was planned against, writes, and reads the result back. A write runs to its end
/// once started: cancellation takes effect between changes. A query rule update adds the new
/// rule before it removes the old one, so the collection never holds fewer rules than before.
/// </summary>
public sealed partial class CollectionService : ICollectionService
{
    public const string LogArea = "Collections";

    /// <summary>Starts the message of an outcome that changed nothing on purpose: a cancelled run or a rule name already taken by an apply.</summary>
    public const string SkippedPrefix = "Skipped: ";

    public const int MemberPageSize = 200;

    public const int MaxPreviewRows = 500;

    public const int MaxNameLength = 255;

    internal const string ReadResource = "ApplicationPackagerEngine.Services.Collections.Scripts.ReadCollections.ps1";
    internal const string WriteResource = "ApplicationPackagerEngine.Services.Collections.Scripts.WriteCollection.ps1";

    internal const string AccessHint =
        "The account needs a ConfigMgr security role with rights on collections and queries (Read, Modify, Create and Delete on Collection and Query).";

    private static readonly ConfigMgrScript ReadScript = ConfigMgrScript.FromResource(ReadResource);
    private static readonly ConfigMgrScript WriteScript = ConfigMgrScript.FromResource(WriteResource);
    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly IConfigMgrSession _session;
    private readonly IAppLog? _log;
    private readonly Func<string> _tempSuffix;

    public CollectionService(IConfigMgrSession session, IAppLog? log = null, Func<string>? tempSuffix = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _log = log;
        _tempSuffix = tempSuffix ?? (() => Guid.NewGuid().ToString("N")[..8]);
    }

    [GeneratedRegex("^[A-Za-z0-9]{1,8}$")]
    private static partial Regex CollectionIdRegex();

    [GeneratedRegex("^[A-Za-z0-9]{1,16}$")]
    private static partial Regex QueryIdRegex();

    [GeneratedRegex("^[0-9A-Fa-f]{16}$")]
    private static partial Regex ScheduleRegex();

    public static bool IsBuiltIn(string? collectionId) =>
        collectionId is not null && collectionId.StartsWith("SMS", StringComparison.OrdinalIgnoreCase);

    public static bool IsBuiltInQuery(string? queryId) =>
        queryId is not null && queryId.StartsWith("SMS", StringComparison.OrdinalIgnoreCase);

    public static bool IsCollectionId(string? id) => id is not null && CollectionIdRegex().IsMatch(id);

    /// <summary>The number of matches at which Validate and Preview stop reading the provider result.</summary>
    public int MatchCountLimit { get; init; } = 10000;

    /// <summary>
    /// The interval of a ConfigMgr interval schedule token (recurrence flag 2 in bits 19 to 21;
    /// minutes in bits 13 to 18, hours in bits 8 to 12, days in bits 3 to 7); null for any
    /// other token or an interval that mixes units.
    /// </summary>
    public static RefreshInterval? IntervalOf(string? token)
    {
        if (token is null || !ScheduleRegex().IsMatch(token)
            || !ulong.TryParse(token, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var v)
            || ((v >> 19) & 0x7) != 2)
            return null;
        var minutes = (int)((v >> 13) & 0x3F);
        var hours = (int)((v >> 8) & 0x1F);
        var days = (int)((v >> 3) & 0x1F);
        return (minutes, hours, days) switch
        {
            ( > 0, 0, 0) => new RefreshInterval(minutes, "Minutes"),
            (0, > 0, 0) => new RefreshInterval(hours, "Hours"),
            (0, 0, > 0) => new RefreshInterval(days, "Days"),
            _ => null
        };
    }

    private static string IntervalText(RefreshInterval? interval) => interval is null
        ? "the current schedule"
        : interval.Count == 1 ? $"every {interval.Unit.ToLowerInvariant().TrimEnd('s')}" : $"every {interval.Count} {interval.Unit.ToLowerInvariant()}";

    // ---- Script output shapes ----------------------------------------------------------------

    internal sealed record ReadRow(
        string? Kind, string? CollectionId, string? Name, string? ObjectPath, string? LimitToCollectionId, string? LimitToCollectionName,
        int MemberCount, int RefreshType, string? RefreshSchedule, string? Comment, int WindowCount,
        int DirectRuleCount, int QueryRuleCount, int IncludeRuleCount, int ExcludeRuleCount);

    internal sealed record RuleRow(string? Kind, string? RuleKind, string? RuleName, string? Expression, string? ResourceId, string? IncludedCollectionId);

    internal sealed record MemberRow(string? Kind, string? ResourceId, string? Name, bool IsClient, bool IsActive, bool IsDirect);

    internal sealed record CountRow(string? Kind, int Count, int Deployments);

    internal sealed record ReferenceRow(string? Kind, string? CollectionId, string? Name, string? Relation);

    internal sealed record WqlRow(string? Kind, bool Valid, int Count, string[]? Columns, string? Detail, bool Capped = false);

    internal sealed record WqlValues(string? Kind, string[]? Values);

    internal sealed record QueryRow(string? Kind, string? QueryId, string? Name, string? Expression, string? TargetClassName, string? Comment, string? LimitToCollectionId);

    internal sealed record FoundRow(string? Kind, string? CollectionId, string? Name, int CollectionType);

    internal sealed record ResourceRow(string? Kind, string? ResourceId, string? Name);

    internal sealed record WriteResult(
        string? Kind, string? Status, string? Detail, string[]? Steps, ReadRow? Collection, RuleRow[]? Rules, QueryRow? Query, string[]? References, string? CreatedId = null);

    private sealed record CollectionState(CollectionRow Row, IReadOnlyList<CollectionRule> Rules, int WindowCount);

    // ---- Reads --------------------------------------------------------------------------------

    public async Task<IReadOnlyList<CollectionRow>> ListCollectionsAsync(CancellationToken ct)
    {
        var output = await ReadAsync(new ConfigMgrParameters { { "Operation", "List" } }, ct).ConfigureAwait(false);
        var rows = Rows<ReadRow>(output, "Collection").Select(r => ToRow(r)).ToList();
        _log?.Info(LogArea, $"Listed {rows.Count} device collections.");
        return rows;
    }

    public async Task<CollectionRow?> GetCollectionAsync(string collectionId, CancellationToken ct)
    {
        var id = CheckedCollectionId(collectionId);
        var state = await ReadStateAsync(id, ct).ConfigureAwait(false);
        _log?.Info(LogArea, state is null ? "Open by ID found no collection." : "Opened one collection by ID.");
        return state?.Row;
    }

    public async Task<CollectionDetail> GetDetailAsync(string collectionId, int memberPage, CancellationToken ct)
    {
        var id = CheckedCollectionId(collectionId);
        var output = await ReadAsync(new ConfigMgrParameters
        {
            { "Operation", "Detail" }, { "CollectionId", id }, { "MemberPage", Math.Max(0, memberPage) }, { "PageSize", MemberPageSize }
        }, ct).ConfigureAwait(false);
        var row = Rows<ReadRow>(output, "Collection").FirstOrDefault()
            ?? throw new CollectionServiceException(CollectionMissingMessage(null, id));
        var rules = Rows<RuleRow>(output, "Rule").Select(ToRule).OfType<CollectionRule>().ToList();
        var members = Rows<MemberRow>(output, "Member").Select(m => new CollectionMember(
            m.ResourceId ?? "", m.Name ?? "", ClientState(m.IsClient, m.IsActive), m.IsDirect)).ToList();
        var total = Rows<CountRow>(output, "MemberTotal").FirstOrDefault()?.Count ?? members.Count;
        _log?.Info(LogArea, $"Read the detail of one collection: {rules.Count} rules, {members.Count} of {total} members.");
        return new CollectionDetail(ToRow(row), rules, members, total);
    }

    public async Task<WqlResult> ValidateWqlAsync(string expression, CancellationToken ct)
    {
        if (WqlGuard.Check(expression) is { } reason) return Invalid(reason);
        var output = await ReadAsync(new ConfigMgrParameters { { "Operation", "Validate" }, { "Expression", expression }, { "MaxCount", Math.Max(1, MatchCountLimit) } }, ct).ConfigureAwait(false);
        var wql = Rows<WqlRow>(output, "Wql").FirstOrDefault();
        if (wql is null) return Invalid("ConfigMgr returned no result for the query.");
        if (!wql.Valid)
        {
            LogDetail("A query was not accepted", wql.Detail);
            _log?.Info(LogArea, "Validated a query: not valid.");
            return Invalid(ProviderRejectedMessage);
        }
        _log?.Info(LogArea, $"Validated a query: valid, {wql.Count} matches{(wql.Capped ? " or more" : "")}.");
        return new WqlResult(true, $"The query is valid. It matches {Matches(wql)}.", wql.Count, [], []);
    }

    private static string Matches(WqlRow wql) =>
        wql.Capped ? $"{wql.Count.ToString(CultureInfo.InvariantCulture)} or more resources (counting stops there)" : Count(wql.Count, "resource");

    public async Task<WqlResult> PreviewWqlAsync(string expression, int maxRows, CancellationToken ct)
    {
        if (WqlGuard.Check(expression) is { } reason) return Invalid(reason);
        var take = Math.Clamp(maxRows, 1, MaxPreviewRows);
        var output = await ReadAsync(new ConfigMgrParameters
        {
            { "Operation", "Preview" }, { "Expression", expression }, { "MaxRows", take }, { "MaxCount", Math.Max(1, MatchCountLimit) }
        }, ct).ConfigureAwait(false);
        var wql = Rows<WqlRow>(output, "Wql").FirstOrDefault();
        if (wql is null) return Invalid("ConfigMgr returned no result for the query.");
        if (!wql.Valid)
        {
            LogDetail("A preview query was not accepted", wql.Detail);
            _log?.Info(LogArea, "Previewed a query: not valid.");
            return Invalid(ProviderRejectedMessage);
        }
        var columns = wql.Columns ?? [];
        var rows = Rows<WqlValues>(output, "WqlRow").Take(take)
            .Select(r => (IReadOnlyList<string>)Enumerable.Range(0, columns.Length).Select(i => r.Values is { } v && i < v.Length ? v[i] ?? "" : "").ToList())
            .ToList();
        _log?.Info(LogArea, $"Previewed a query: {wql.Count} matches{(wql.Capped ? " or more" : "")}, {rows.Count} rows shown.");
        var message = rows.Count < wql.Count
            ? $"The query matches {Matches(wql)}. The first {rows.Count} are shown."
            : $"The query matches {Matches(wql)}.";
        return new WqlResult(true, message, wql.Count, columns, rows);
    }

    public async Task<IReadOnlyList<SavedQuery>> ListSavedQueriesAsync(CancellationToken ct)
    {
        var output = await ReadAsync(new ConfigMgrParameters { { "Operation", "SavedQueries" } }, ct).ConfigureAwait(false);
        var queries = Rows<QueryRow>(output, "SavedQuery").Select(ToQuery)
            .OrderBy(q => q.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(q => q.QueryId, StringComparer.OrdinalIgnoreCase).ToList();
        _log?.Info(LogArea, $"Listed {queries.Count} saved queries.");
        return queries;
    }

    private const string ProviderRejectedMessage =
        "The SMS Provider did not accept the query. Check the class and property names and the syntax.";

    private static WqlResult Invalid(string message) => new(false, message, 0, [], []);

    private static string ClientState(bool isClient, bool isActive) =>
        !isClient ? "No client" : isActive ? "Client, active" : "Client, inactive";

    // ---- Prepare ------------------------------------------------------------------------------

    public async Task<CollectionChange> PrepareAsync(CollectionChange request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        request = Normalized(request);
        var impact = (request.Impact ?? []).ToList();
        switch (request.Kind)
        {
            case CollectionChangeKind.Create:
                return await PrepareCreateAsync(request, impact, ct).ConfigureAwait(false);
            case CollectionChangeKind.Copy:
            {
                var source = await RequireStateAsync(request, ct).ConfigureAwait(false);
                var name = CheckedName(request.AfterRow?.Name, "Enter a name for the copy.");
                await CheckNameFreeAsync(name, ct).ConfigureAwait(false);
                impact.Add($"Copies collection {Shown(source.Row)} with its rules to a new collection named {name}.");
                impact.Add("Undo removes the copy.");
                return request with
                {
                    CollectionId = source.Row.CollectionId, CollectionName = source.Row.Name, BeforeRow = source.Row,
                    AfterRow = source.Row with { CollectionId = "", Name = name, IsBuiltIn = false }, Impact = impact
                };
            }
            case CollectionChangeKind.SetProperties:
                return await PrepareSetAsync(request, impact, ct).ConfigureAwait(false);
            case CollectionChangeKind.AddRule:
            case CollectionChangeKind.ApplyTemplate:
                return await PrepareAddRuleAsync(request, impact, ct).ConfigureAwait(false);
            case CollectionChangeKind.UpdateRule:
            {
                RefuseBuiltIn(request);
                var state = await RequireStateAsync(request, ct).ConfigureAwait(false);
                if (request.RuleBefore is not { Kind: CollectionRuleKind.Query } before || request.RuleAfter is not { Kind: CollectionRuleKind.Query } after)
                    throw new CollectionServiceException("Select the query rule to update.");
                var current = QueryRule(state.Rules, before.RuleName)
                    ?? throw new CollectionServiceException(RuleMissingMessage(state.Row, before));
                if (!string.Equals(current.Expression, before.Expression, StringComparison.Ordinal))
                    throw new CollectionServiceException(RuleChangedMessage(state.Row, before));
                var target = CheckedRule(after);
                if (!string.Equals(target.RuleName, current.RuleName, StringComparison.OrdinalIgnoreCase) && QueryRule(state.Rules, target.RuleName) is not null)
                    throw new CollectionServiceException(RuleTakenMessage(state.Row, target.RuleName));
                RefuseUnrestorable(state.Row, current, "changed");
                var orphans = (request.Rules ?? [])
                    .Where(o => o.Kind == CollectionRuleKind.Query && !string.Equals(o.RuleName, target.RuleName, StringComparison.OrdinalIgnoreCase))
                    .Select(o => QueryRule(state.Rules, o.RuleName) is { } r && string.Equals(r.Expression, o.Expression, StringComparison.Ordinal) ? r : null)
                    .OfType<CollectionRule>().ToList();
                impact.Add($"Replaces query rule {current.RuleName} of collection {Shown(state.Row)}.");
                impact.Add($"Old WQL: {current.Expression}");
                impact.Add($"New WQL: {target.Expression}");
                impact.Add("The new rule is added before the old rule is removed, so the collection never has fewer rules. If a step fails, the old rule stays.");
                foreach (var orphan in orphans) impact.Add($"Also removes query rule {orphan.RuleName}, which an earlier update left.");
                impact.Add("Undo puts the old rule back.");
                return request with
                {
                    CollectionId = state.Row.CollectionId, CollectionName = state.Row.Name, BeforeRow = state.Row, RuleBefore = current, RuleAfter = target,
                    Rules = orphans.Count == 0 ? null : orphans, Impact = impact
                };
            }
            case CollectionChangeKind.RemoveRule:
            {
                RefuseBuiltIn(request);
                var state = await RequireStateAsync(request, ct).ConfigureAwait(false);
                var before = request.RuleBefore ?? throw new CollectionServiceException("Select the rule to remove.");
                var current = FindRule(state.Rules, before) ?? throw new CollectionServiceException(RuleMissingMessage(state.Row, before));
                if (current.Kind == CollectionRuleKind.Query && !string.Equals(current.Expression, before.Expression, StringComparison.Ordinal))
                    throw new CollectionServiceException(RuleChangedMessage(state.Row, before));
                RefuseUnrestorable(state.Row, current, "removed");
                impact.Add($"Removes {RuleText(current)} from collection {Shown(state.Row)}.");
                if (current.Kind == CollectionRuleKind.Query) impact.Add($"WQL: {current.Expression}");
                if (current.Kind is CollectionRuleKind.Include or CollectionRuleKind.Exclude) impact.Add(MembershipLine(current.Kind));
                impact.Add("Undo adds the rule again.");
                return request with { CollectionId = state.Row.CollectionId, CollectionName = state.Row.Name, BeforeRow = state.Row, RuleBefore = current, Impact = impact };
            }
            case CollectionChangeKind.Evaluate:
            {
                var state = await RequireStateAsync(request, ct).ConfigureAwait(false);
                impact.Add($"Starts a membership evaluation of collection {Shown(state.Row)} ({Count(state.Row.MemberCount, "member")} now).");
                impact.Add("An evaluation starts work on the site server. A large collection or a complex query adds load there.");
                impact.Add("Undo: not applicable.");
                return request with { CollectionId = state.Row.CollectionId, CollectionName = state.Row.Name, BeforeRow = state.Row, Impact = impact };
            }
            case CollectionChangeKind.Remove:
                return await PrepareRemoveAsync(request, impact, ct).ConfigureAwait(false);
            case CollectionChangeKind.SaveQuery:
            {
                var query = CheckedQuery(request.QueryAfter);
                await CheckQueryNameFreeAsync(query.Name, null, ct).ConfigureAwait(false);
                impact.Add($"Saves a new site query named {query.Name}.");
                impact.Add($"WQL: {query.Expression}");
                impact.Add("Undo deletes the saved query.");
                return request with { QueryBefore = null, QueryAfter = query, Impact = impact };
            }
            case CollectionChangeKind.UpdateQuery:
            {
                var current = await RequireQueryAsync(request.QueryBefore, ct).ConfigureAwait(false);
                if (!SameQuery(current, request.QueryBefore!))
                    throw new CollectionServiceException(QueryChangedMessage(current.Name));
                RefuseUnrestorable(current, "changed");
                var query = CheckedQuery(request.QueryAfter) with { QueryId = current.QueryId };
                if (!string.Equals(query.Name, current.Name, StringComparison.OrdinalIgnoreCase))
                    await CheckQueryNameFreeAsync(query.Name, current.QueryId, ct).ConfigureAwait(false);
                if (SameQuery(current, query))
                    throw new CollectionServiceException("The saved query has the same values in ConfigMgr. Nothing to change.");
                if (!string.Equals(current.Name, query.Name, StringComparison.Ordinal)) impact.Add($"Name: {current.Name} -> {query.Name}");
                impact.Add($"Old WQL: {current.Expression}");
                impact.Add($"New WQL: {query.Expression}");
                if (!string.Equals(current.Comment, query.Comment, StringComparison.Ordinal)) impact.Add($"Comment: {Blank(current.Comment)} -> {Blank(query.Comment)}");
                impact.Add("Undo sets the old values back.");
                return request with { QueryBefore = current, QueryAfter = query, Impact = impact };
            }
            case CollectionChangeKind.DeleteQuery:
            {
                var current = await RequireQueryAsync(request.QueryBefore, ct).ConfigureAwait(false);
                RefuseUnrestorable(current, "deleted");
                impact.Add($"Deletes the saved query {current.Name} ({current.QueryId}).");
                impact.Add($"WQL: {current.Expression}");
                impact.Add("Undo saves the query again with a new query ID.");
                return request with { QueryBefore = current, QueryAfter = null, Impact = impact };
            }
            default:
                throw new CollectionServiceException("The change kind is not known.");
        }
    }

    private async Task<CollectionChange> PrepareCreateAsync(CollectionChange request, List<string> impact, CancellationToken ct)
    {
        var after = request.AfterRow ?? throw new CollectionServiceException("The change has no collection values.");
        var name = CheckedName(after.Name, "Enter a collection name.");
        var refresh = CheckedRefreshType(after.RefreshType);
        if (!IsCollectionId(after.LimitingCollectionId)) throw new CollectionServiceException("Select a limiting collection.");
        var limiting = await ReadStateAsync(after.LimitingCollectionId, ct).ConfigureAwait(false)
            ?? throw new CollectionServiceException(CollectionMissingMessage(after.LimitingCollectionName, after.LimitingCollectionId));
        await CheckNameFreeAsync(name, ct).ConfigureAwait(false);
        var rules = (request.Rules ?? (request.RuleAfter is null ? [] : [request.RuleAfter])).Select(CheckedRule).ToList();
        foreach (var rule in rules.Where(r => r.Kind is CollectionRuleKind.Include or CollectionRuleKind.Exclude))
            if (await ReadStateAsync(rule.IncludedCollectionId, ct).ConfigureAwait(false) is null)
                throw new CollectionServiceException(CollectionMissingMessage(null, rule.IncludedCollectionId));

        if (RefreshInterval.Check(after.RefreshInterval) is { } invalidInterval) throw new CollectionServiceException(invalidInterval);
        var periodic = refresh is "Periodic" or "Both";
        // A captured schedule token (the Undo of a remove) is kept so the schedule comes back exactly.
        var token = ScheduleOrEmpty(after.RefreshSchedule);
        if (after.RefreshInterval is { } given && IntervalOf(token) != given) token = "";
        var interval = !periodic ? null : token.Length > 0 ? IntervalOf(token) : after.RefreshInterval ?? RefreshInterval.Default;

        var row = after with
        {
            CollectionId = "", Name = name, IsBuiltIn = false, RefreshType = refresh,
            LimitingCollectionId = limiting.Row.CollectionId, LimitingCollectionName = limiting.Row.Name,
            RefreshSchedule = token, RefreshInterval = interval, Comment = after.Comment ?? ""
        };
        impact.Add($"Creates collection {name}, limited to {Shown(limiting.Row)}.");
        impact.Add(!periodic ? $"Refresh: {RefreshText(refresh)}."
            : interval is null ? $"Refresh: {RefreshText(refresh)}, with the schedule the collection had."
            : token.Length == 0 && after.RefreshInterval is null ? $"Refresh: {RefreshText(refresh)}, {IntervalText(interval)} (the default when no interval is given)."
            : $"Refresh: {RefreshText(refresh)}, {IntervalText(interval)}.");
        if (rules.Count > 0) impact.Add($"Adds {Count(rules.Count, "rule")}: {string.Join("; ", rules.Select(RuleText))}.");
        impact.Add("Undo removes the new collection.");
        return request with { CollectionId = "", CollectionName = name, BeforeRow = null, AfterRow = row, Rules = rules, RuleAfter = null, Impact = impact };
    }

    private async Task<CollectionChange> PrepareSetAsync(CollectionChange request, List<string> impact, CancellationToken ct)
    {
        RefuseBuiltIn(request);
        var baseCount = impact.Count;
        var state = await RequireStateAsync(request, ct).ConfigureAwait(false);
        var current = state.Row;
        var after = request.AfterRow ?? throw new CollectionServiceException("The change has no collection values.");
        var name = CheckedName(after.Name, "Enter a collection name.");
        var refresh = CheckedRefreshType(after.RefreshType);
        if (!IsCollectionId(after.LimitingCollectionId)) throw new CollectionServiceException("Select a limiting collection.");
        if (string.Equals(after.LimitingCollectionId, current.CollectionId, StringComparison.OrdinalIgnoreCase))
            throw new CollectionServiceException("A collection cannot be its own limiting collection.");
        var limiting = await ReadStateAsync(after.LimitingCollectionId, ct).ConfigureAwait(false)
            ?? throw new CollectionServiceException(CollectionMissingMessage(after.LimitingCollectionName, after.LimitingCollectionId));
        if (!string.Equals(name, current.Name, StringComparison.OrdinalIgnoreCase))
            await CheckNameFreeAsync(name, ct).ConfigureAwait(false);
        if (RefreshInterval.Check(after.RefreshInterval) is { } invalidInterval) throw new CollectionServiceException(invalidInterval);
        var periodic = refresh is "Periodic" or "Both";
        var currentInterval = IntervalOf(current.RefreshSchedule);
        var newInterval = periodic && after.RefreshInterval is { } wanted && wanted != currentInterval ? wanted : null;
        if (periodic && newInterval is null && current.RefreshSchedule.Length == 0) newInterval = after.RefreshInterval ?? RefreshInterval.Default;

        var row = current with
        {
            Name = name, Comment = after.Comment ?? "", LimitingCollectionId = limiting.Row.CollectionId,
            LimitingCollectionName = limiting.Row.Name, RefreshType = refresh,
            RefreshSchedule = newInterval is null ? current.RefreshSchedule : "", RefreshInterval = newInterval ?? currentInterval
        };
        if (!string.Equals(current.Name, row.Name, StringComparison.Ordinal)) impact.Add($"Name: {current.Name} -> {row.Name}");
        if (!string.Equals(current.Comment, row.Comment, StringComparison.Ordinal)) impact.Add($"Comment: {Blank(current.Comment)} -> {Blank(row.Comment)}");
        if (!string.Equals(current.LimitingCollectionId, row.LimitingCollectionId, StringComparison.OrdinalIgnoreCase))
            impact.Add($"Limiting collection: {Shown(current.LimitingCollectionName, current.LimitingCollectionId)} -> {Shown(row.LimitingCollectionName, row.LimitingCollectionId)}");
        if (!string.Equals(current.RefreshType, row.RefreshType, StringComparison.Ordinal)) impact.Add($"Refresh: {RefreshText(current.RefreshType)} -> {RefreshText(row.RefreshType)}");
        if (newInterval is not null)
            impact.Add(currentInterval is null && current.RefreshSchedule.Length == 0 && after.RefreshInterval is null
                ? $"Refresh schedule: none -> {IntervalText(newInterval)} (the default when no interval is given)"
                : $"Refresh schedule: {IntervalText(currentInterval)} -> {IntervalText(newInterval)}");

        if (impact.Count == baseCount)
            throw new CollectionServiceException("The properties are the same as in ConfigMgr. Nothing to change.");
        impact.Add("Undo sets the old values back.");
        return request with { CollectionId = current.CollectionId, CollectionName = current.Name, BeforeRow = current, AfterRow = row, Impact = impact };
    }

    private async Task<CollectionChange> PrepareAddRuleAsync(CollectionChange request, List<string> impact, CancellationToken ct)
    {
        RefuseBuiltIn(request);
        var rule = CheckedRule(request.RuleAfter ?? throw new CollectionServiceException("The change has no rule."));
        var state = await RequireStateAsync(request, ct).ConfigureAwait(false);
        var change = request with { CollectionId = state.Row.CollectionId, CollectionName = state.Row.Name, BeforeRow = state.Row, RuleBefore = null };
        switch (rule.Kind)
        {
            case CollectionRuleKind.Query:
                if (QueryRule(state.Rules, rule.RuleName) is { } taken)
                {
                    if (request.Kind == CollectionChangeKind.AddRule) throw new CollectionServiceException(RuleTakenMessage(state.Row, rule.RuleName));
                    impact.Add(SkippedPrefix + $"collection {Shown(state.Row)} already has a query rule named {rule.RuleName}.");
                    return change with { RuleBefore = taken, RuleAfter = rule, Impact = impact };
                }
                impact.Add($"Adds query rule {rule.RuleName} to collection {Shown(state.Row)}.");
                impact.Add($"WQL: {rule.Expression}");
                break;
            case CollectionRuleKind.Direct:
            {
                var output = await ReadAsync(new ConfigMgrParameters { { "Operation", "Resource" }, { "ResourceId", long.Parse(rule.ResourceId, CultureInfo.InvariantCulture) } }, ct).ConfigureAwait(false);
                var resource = Rows<ResourceRow>(output, "Resource").FirstOrDefault()
                    ?? throw new CollectionServiceException($"No device with resource ID {rule.ResourceId} exists in ConfigMgr.");
                if (FindRule(state.Rules, rule) is not null)
                    throw new CollectionServiceException($"Collection {Shown(state.Row)} already has a direct rule for {resource.Name}.");
                rule = rule with { RuleName = resource.Name ?? rule.RuleName, Detail = $"Resource {rule.ResourceId}" };
                impact.Add($"Adds device {resource.Name} ({rule.ResourceId}) as a direct member of collection {Shown(state.Row)}.");
                break;
            }
            default:
            {
                if (string.Equals(rule.IncludedCollectionId, state.Row.CollectionId, StringComparison.OrdinalIgnoreCase))
                    throw new CollectionServiceException("A collection cannot include or exclude itself.");
                var other = await ReadStateAsync(rule.IncludedCollectionId, ct).ConfigureAwait(false)
                    ?? throw new CollectionServiceException(CollectionMissingMessage(rule.RuleName, rule.IncludedCollectionId));
                if (FindRule(state.Rules, rule) is not null)
                    throw new CollectionServiceException($"Collection {Shown(state.Row)} already has an {KindWord(rule.Kind)} rule for {Shown(other.Row)}.");
                rule = rule with { RuleName = other.Row.Name, Detail = $"Collection {other.Row.CollectionId}" };
                impact.Add($"Adds an {KindWord(rule.Kind)} rule for collection {Shown(other.Row)} to collection {Shown(state.Row)}.");
                impact.Add(MembershipLine(rule.Kind));
                break;
            }
        }
        impact.Add("Undo removes the rule.");
        return change with { RuleAfter = rule, Impact = impact };
    }

    private async Task<CollectionChange> PrepareRemoveAsync(CollectionChange request, List<string> impact, CancellationToken ct)
    {
        RefuseBuiltIn(request);
        var id = CheckedCollectionId(request.CollectionId);
        var output = await ReadAsync(new ConfigMgrParameters { { "Operation", "Impact" }, { "CollectionId", id } }, ct).ConfigureAwait(false);
        var read = Rows<ReadRow>(output, "Collection").FirstOrDefault()
            ?? throw new CollectionServiceException(CollectionMissingMessage(request.CollectionName, id));
        var row = ToRow(read);
        var rules = Rows<RuleRow>(output, "Rule").Select(ToRule).OfType<CollectionRule>().ToList();
        var deployments = Rows<CountRow>(output, "Impact").FirstOrDefault()?.Deployments ?? 0;
        var references = Rows<ReferenceRow>(output, "Reference").ToList();

        string Names(IEnumerable<ReferenceRow> refs)
        {
            var list = refs.Select(r => Shown(r.Name, r.CollectionId ?? "")).ToList();
            return string.Join(", ", list.Take(5)) + (list.Count > 5 ? $" and {list.Count - 5} more" : "");
        }

        var limited = references.Where(r => r.Relation == "Limiting").ToList();
        if (limited.Count > 0)
            throw new CollectionServiceException(
                $"Collection {Shown(row)} is the limiting collection of {Count(limited.Count, "collection")} ({Names(limited)}), so it is not removed. Change their limiting collection first.");
        var included = references.Where(r => r.Relation is "Include" or "Exclude").ToList();
        if (included.Count > 0)
            throw new CollectionServiceException(
                $"{Count(included.Count, "collection")} include or exclude collection {Shown(row)} ({Names(included)}). ConfigMgr does not remove a collection that is in such a rule. Remove those rules first.");

        if (InvalidCreate(row with { CollectionId = "" }, []) is { } rowReason)
            throw new CollectionServiceException($"Undo could not create collection {Shown(row)} again, so it is not removed. {rowReason}");
        foreach (var rule in rules) RefuseUnrestorable(row, rule, "removed");

        impact.Add($"Removes collection {Shown(row)}.");
        impact.Add($"Members now: {row.MemberCount}.");
        impact.Add($"Deployments to the collection: {deployments}. ConfigMgr removes them with the collection.");
        impact.Add($"Maintenance windows on the collection: {read.WindowCount}. ConfigMgr removes them with the collection.");
        impact.Add("Collections that include or exclude it: 0. Collections that use it as limiting collection: 0.");
        impact.Add($"Rules captured for Undo: {rules.Count}.");
        impact.Add("If these counts or the properties change before the remove starts, the collection is not removed.");
        impact.Add("Undo creates the collection again with its properties and rules. The new collection has a new ID. Deployments and maintenance windows are not restored.");
        return request with
        {
            CollectionId = row.CollectionId, CollectionName = row.Name, BeforeRow = row, AfterRow = null, Rules = rules, Impact = impact,
            RemoveCounts = new CollectionRemoveCounts(row.MemberCount, deployments, read.WindowCount)
        };
    }

    // ---- Apply --------------------------------------------------------------------------------

    public async Task<IReadOnlyList<CollectionOutcome>> ApplyAsync(
        IReadOnlyList<CollectionChange> changes, IProgress<CollectionOutcome> progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(progress);
        var outcomes = new List<CollectionOutcome>(changes.Count);
        var skipped = 0;
        foreach (var change in changes)
        {
            CollectionOutcome outcome;
            if (ct.IsCancellationRequested)
            {
                outcome = new CollectionOutcome(change, false, SkippedPrefix + "the run was cancelled before this change.", null);
                skipped++;
            }
            else
            {
                try
                {
                    outcome = await ApplyOneAsync(Normalized(change)).ConfigureAwait(false) with { Change = change };
                }
                catch (Exception ex)
                {
                    _log?.Warning(LogArea, $"{change.Kind} stopped with an unexpected error: {ex.GetType().Name}.");
                    outcome = Fail(change, $"The change to {Target(change)} stopped with an unexpected error. Refresh the list to see the current state. The log has the details.");
                }
                if (outcome.Message.StartsWith(SkippedPrefix, StringComparison.Ordinal)) skipped++;
                _log?.Info(LogArea, $"{change.Kind}: {(outcome.Succeeded ? "succeeded" : "not applied")}.");
            }
            outcomes.Add(outcome);
            progress.Report(outcome);
        }
        var succeeded = outcomes.Count(o => o.Succeeded);
        _log?.Info(LogArea, $"Applied {changes.Count} changes: {succeeded} succeeded, {outcomes.Count - succeeded - skipped} failed, {skipped} skipped.");
        return outcomes;
    }

    private async Task<CollectionOutcome> ApplyOneAsync(CollectionChange change)
    {
        switch (change.Kind)
        {
            case CollectionChangeKind.Create:
            {
                if (change.AfterRow is not { } after || InvalidCreate(after, change.Rules) is { } invalid)
                    return Refused(change, InvalidCreate(change.AfterRow, change.Rules) ?? NotPreparedMessage);
                var rules = change.Rules ?? (change.RuleAfter is null ? [] : [change.RuleAfter]);
                var parameters = new ConfigMgrParameters
                {
                    { "Operation", "Create" }, { "Name", after.Name.Trim() }, { "LimitingCollectionId", after.LimitingCollectionId },
                    { "RefreshType", after.RefreshType }, { "Comment", after.Comment ?? "" },
                    { "RuleKinds", rules.Select(r => r.Kind.ToString()).ToArray() },
                    { "RuleNames", rules.Select(r => r.RuleName ?? "").ToArray() },
                    { "RuleExpressions", rules.Select(r => r.Expression ?? "").ToArray() },
                    { "RuleResourceIds", rules.Select(r => r.ResourceId ?? "").ToArray() },
                    { "RuleCollectionIds", rules.Select(r => r.IncludedCollectionId ?? "").ToArray() }
                };
                AddSchedule(parameters, after, after.RefreshInterval ?? RefreshInterval.Default);
                var existed = await CollectionIdsNamedAsync(after.Name.Trim()).ConfigureAwait(false);
                var run = await RunWriteAsync(change, parameters).ConfigureAwait(false);
                if (run.Failure is not null) return await ReconcileAsync(change, run, existed).ConfigureAwait(false);
                var created = StateOf(run.Result!) ?? await ReadCreatedAsync(run.Result!.CreatedId).ConfigureAwait(false);
                if (created is null) return await ReconcileAsync(change, Unread(change, run), existed).ConfigureAwait(false);
                var undo = RemoveUndo(created);
                if (run.Result!.Status == "rules-partial")
                {
                    var missing = (run.Result.Detail ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(i => int.TryParse(i, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n < rules.Count ? RuleText(rules[n]) : null)
                        .OfType<string>().ToList();
                    _log?.Warning(LogArea, $"Create added the collection but {missing.Count} of {rules.Count} rules failed.");
                    return new CollectionOutcome(change, false,
                        $"Created collection {Shown(created.Row)}, but ConfigMgr did not add {Count(missing.Count, "rule")}: {string.Join("; ", missing)}. Undo removes the new collection.", undo);
                }
                return Done(change, $"Created collection {Shown(created.Row)}.", undo);
            }
            case CollectionChangeKind.Copy:
            {
                if (!IsCollectionId(change.CollectionId)) return Refused(change, NotPreparedMessage);
                if (CheckName(change.AfterRow?.Name) is { } invalid) return Refused(change, invalid);
                var name = change.AfterRow!.Name.Trim();
                var existed = await CollectionIdsNamedAsync(name).ConfigureAwait(false);
                var run = await RunWriteAsync(change, new ConfigMgrParameters { { "Operation", "Copy" }, { "CollectionId", change.CollectionId }, { "Name", name } }).ConfigureAwait(false);
                if (run.Failure is not null) return await ReconcileAsync(change, run, existed).ConfigureAwait(false);
                var copy = StateOf(run.Result!) ?? await ReadCreatedAsync(run.Result!.CreatedId).ConfigureAwait(false);
                if (copy is null) return await ReconcileAsync(change, Unread(change, run), existed).ConfigureAwait(false);
                return Done(change, $"Copied collection {Shown(change.CollectionName, change.CollectionId)} to {Shown(copy.Row)}.", RemoveUndo(copy));
            }
            case CollectionChangeKind.SetProperties:
            {
                if (change.BeforeRow is not { } before || change.AfterRow is not { } after) return Refused(change, NotPreparedMessage);
                if (IsBuiltIn(change.CollectionId)) return Refused(change, BuiltInMessage(change));
                if (CheckName(after.Name) is { } invalidName) return Refused(change, invalidName);
                if (!IsCollectionId(after.LimitingCollectionId) || !IsRefreshType(after.RefreshType)) return Refused(change, NotPreparedMessage);
                if (RefreshInterval.Check(after.RefreshInterval) is { } invalidInterval) return Refused(change, invalidInterval);
                var parameters = Expected(change, before);
                parameters["Operation"] = "SetProperties";
                parameters["Name"] = after.Name.Trim();
                parameters["Comment"] = after.Comment ?? "";
                parameters["LimitingCollectionId"] = after.LimitingCollectionId;
                parameters["RefreshType"] = after.RefreshType;
                AddSchedule(parameters, after, null);
                var run = await RunWriteAsync(change, parameters).ConfigureAwait(false);
                if (run.Failure is not null) return await ReconcileAsync(change, run).ConfigureAwait(false);
                var now = StateOf(run.Result!);
                if (now is null) return await ReconcileAsync(change, Unread(change, run)).ConfigureAwait(false);
                return Done(change, $"Updated the properties of collection {Shown(now.Row)}.", SetUndo(now.Row, before));
            }
            case CollectionChangeKind.AddRule:
            case CollectionChangeKind.ApplyTemplate:
            {
                if (change.Kind == CollectionChangeKind.ApplyTemplate && change.RuleBefore is not null)
                    return Refused(change, $"Collection {Shown(change.CollectionName, change.CollectionId)} already has a query rule named {change.RuleBefore.RuleName}.");
                if (IsBuiltIn(change.CollectionId)) return Refused(change, BuiltInMessage(change));
                if (change.RuleAfter is not { } rule || InvalidRule(rule) is not null) return Refused(change, InvalidRule(change.RuleAfter) ?? NotPreparedMessage);
                var parameters = RuleParameters("AddRule", change.CollectionId, rule);
                parameters["RuleName"] = rule.RuleName ?? "";
                parameters["Expression"] = rule.Expression ?? "";
                var run = await RunWriteAsync(change, parameters).ConfigureAwait(false);
                if (run.Failure is not null) return await ReconcileAsync(change, run).ConfigureAwait(false);
                var added = StateOf(run.Result!) is { } now ? FindRule(now.Rules, rule) : null;
                if (added is null) return await ReconcileAsync(change, Unread(change, run)).ConfigureAwait(false);
                return Done(change, $"Added {RuleText(added)} to collection {Shown(change.CollectionName, change.CollectionId)}.",
                    RuleUndo(CollectionChangeKind.RemoveRule, change, added, null));
            }
            case CollectionChangeKind.RemoveRule:
            {
                if (change.RuleBefore is not { } rule) return Refused(change, NotPreparedMessage);
                if (IsBuiltIn(change.CollectionId)) return Refused(change, BuiltInMessage(change));
                var parameters = RuleParameters("RemoveRule", change.CollectionId, rule);
                parameters["OldRuleName"] = rule.RuleName ?? "";
                parameters["OldExpression"] = rule.Expression ?? "";
                var run = await RunWriteAsync(change, parameters).ConfigureAwait(false);
                if (run.Failure is not null) return await ReconcileAsync(change, run).ConfigureAwait(false);
                return Done(change, $"Removed {RuleText(rule)} from collection {Shown(change.CollectionName, change.CollectionId)}.",
                    RuleUndo(CollectionChangeKind.AddRule, change, null, rule));
            }
            case CollectionChangeKind.UpdateRule:
                return await ApplyUpdateRuleAsync(change).ConfigureAwait(false);
            case CollectionChangeKind.Evaluate:
            {
                if (!IsCollectionId(change.CollectionId)) return Refused(change, NotPreparedMessage);
                var run = await RunWriteAsync(change, new ConfigMgrParameters { { "Operation", "Evaluate" }, { "CollectionId", change.CollectionId } }).ConfigureAwait(false);
                if (run.Failure is not null) return run.Failure;
                return Done(change, $"Started a membership evaluation of collection {Shown(change.CollectionName, change.CollectionId)}. Undo is not applicable.", null);
            }
            case CollectionChangeKind.Remove:
            {
                if (change.BeforeRow is not { } before || change.Rules is not { } rules || change.RemoveCounts is not { } counts) return Refused(change, NotPreparedMessage);
                if (IsBuiltIn(change.CollectionId)) return Refused(change, BuiltInMessage(change));
                var parameters = Expected(change, before);
                parameters["Operation"] = "Remove";
                parameters["ExpectedRuleKeys"] = rules.Select(RuleKey).ToArray();
                parameters["ExpectedMemberCount"] = counts.Members;
                parameters["ExpectedDeployments"] = counts.Deployments;
                parameters["ExpectedWindows"] = counts.MaintenanceWindows;
                var run = await RunWriteAsync(change, parameters).ConfigureAwait(false);
                if (run.Failure is not null) return await ReconcileAsync(change, run).ConfigureAwait(false);
                return Done(change, $"Removed collection {Shown(before)}. {RemoveUndoNote}", CreateUndo(before, rules));
            }
            case CollectionChangeKind.SaveQuery:
            {
                if (change.QueryAfter is not { } query || InvalidQuery(query) is not null) return Refused(change, InvalidQuery(change.QueryAfter) ?? NotPreparedMessage);
                var existed = await QueryIdsNamedAsync(query.Name.Trim()).ConfigureAwait(false);
                var run = await RunWriteAsync(change, QueryParameters("SaveQuery", query, null)).ConfigureAwait(false);
                if (run.Failure is not null) return await ReconcileAsync(change, run, existed).ConfigureAwait(false);
                var saved = run.Result!.Query is { } row ? ToQuery(row) : await ReadCreatedQueryAsync(run.Result.CreatedId).ConfigureAwait(false);
                if (saved is null) return await ReconcileAsync(change, Unread(change, run), existed).ConfigureAwait(false);
                var created = saved;
                return Done(change, $"Saved query {created.Name} ({created.QueryId}).", QueryUndo(CollectionChangeKind.DeleteQuery, created, null));
            }
            case CollectionChangeKind.UpdateQuery:
            {
                if (change.QueryBefore is not { } before || change.QueryAfter is not { } after || InvalidQuery(after) is not null)
                    return Refused(change, InvalidQuery(change.QueryAfter) ?? NotPreparedMessage);
                if (IsBuiltInQuery(before.QueryId)) return Refused(change, BuiltInQueryMessage(before));
                var run = await RunWriteAsync(change, QueryParameters("UpdateQuery", after with { QueryId = before.QueryId }, before)).ConfigureAwait(false);
                if (run.Failure is not null) return await ReconcileAsync(change, run).ConfigureAwait(false);
                if (run.Result!.Query is not { } saved) return await ReconcileAsync(change, Unread(change, run)).ConfigureAwait(false);
                var updated = ToQuery(saved);
                return Done(change, $"Updated saved query {updated.Name} ({updated.QueryId}).", QueryUndo(CollectionChangeKind.UpdateQuery, updated, before));
            }
            case CollectionChangeKind.DeleteQuery:
            {
                if (change.QueryBefore is not { } before) return Refused(change, NotPreparedMessage);
                if (IsBuiltInQuery(before.QueryId)) return Refused(change, BuiltInQueryMessage(before));
                var parameters = QueryParameters("DeleteQuery", before, before);
                var run = await RunWriteAsync(change, parameters).ConfigureAwait(false);
                if (run.Failure is not null) return await ReconcileAsync(change, run).ConfigureAwait(false);
                return Done(change, $"Deleted saved query {before.Name} ({before.QueryId}). Undo saves it again with a new query ID.",
                    QueryUndo(CollectionChangeKind.SaveQuery, null, before));
            }
            default:
                return Fail(change, "The change kind is not known.");
        }
    }

    private const string RemoveUndoNote =
        "Undo creates it again with its properties and rules as a new collection with a new ID; deployments and maintenance windows are not restored.";

    private async Task<CollectionOutcome> ApplyUpdateRuleAsync(CollectionChange change)
    {
        if (change.RuleBefore is not { Kind: CollectionRuleKind.Query } before || change.RuleAfter is not { Kind: CollectionRuleKind.Query } after)
            return Refused(change, NotPreparedMessage);
        if (IsBuiltIn(change.CollectionId)) return Refused(change, BuiltInMessage(change));
        if (InvalidRule(after) is { } invalid) return Refused(change, invalid);
        var sameName = string.Equals(before.RuleName, after.RuleName, StringComparison.OrdinalIgnoreCase);
        var newName = sameName ? before.RuleName : after.RuleName.Trim();
        var temp = $"{newName} (update {_tempSuffix()})";
        var parameters = new ConfigMgrParameters
        {
            { "Operation", "UpdateRule" }, { "CollectionId", change.CollectionId }, { "RuleKind", "Query" },
            { "RuleName", newName }, { "Expression", after.Expression }, { "OldRuleName", before.RuleName },
            { "OldExpression", before.Expression }, { "TempRuleName", temp }
        };
        var orphans = (change.Rules ?? []).Where(r => r.Kind == CollectionRuleKind.Query).ToList();
        parameters["OrphanRuleNames"] = orphans.Select(r => r.RuleName ?? "").ToArray();
        parameters["OrphanExpressions"] = orphans.Select(r => r.Expression ?? "").ToArray();
        var collection = Shown(change.CollectionName, change.CollectionId);
        var run = await RunWriteAsync(change, parameters).ConfigureAwait(false);
        if (run.Result is { Status: "update-failed", Steps: { } steps } && steps.Contains("done"))
        {
            LogDetail("A temporary query rule was not removed", run.Result.Detail);
            var left = string.Join(", ", orphans.Select(o => o.RuleName));
            return Partial(change, $"Rule {newName} on {collection} has the WQL of this change, but ConfigMgr did not remove rule {left}. Remove it in the console.", null);
        }
        if (run.Result?.Status == "update-failed" || (run.Failure is not null && run.Uncertain))
        {
            LogDetail("A query rule update stopped", run.Result?.Detail);
            var rules = run.Result?.Status == "update-failed" ? StateOf(run.Result)?.Rules : (await ReadBackAsync(change).ConfigureAwait(false))?.Rules;
            if (rules is null)
                return Fail(change, $"The result of the update of rule {before.RuleName} on {collection} is not known. Refresh the list to see which rules exist.");
            return UpdateRuleOutcome(change, before, newName, temp, rules, collection);
        }
        if (run.Failure is not null) return run.Failure;
        var updated = StateOf(run.Result!) is { } now ? QueryRule(now.Rules, newName) : null;
        if (updated is null)
        {
            var rules = (await ReadBackAsync(change).ConfigureAwait(false))?.Rules;
            if (rules is null) return Fail(change, NotReadBackMessage(change));
            return UpdateRuleOutcome(change, before, newName, temp, rules, collection);
        }
        return Done(change, $"Updated query rule {updated.RuleName} on {collection}.",
            RuleUndo(CollectionChangeKind.UpdateRule, change, updated, before));
    }

    /// <summary>The outcome of an update that stopped part way, from the rules the collection holds now.</summary>
    private CollectionOutcome UpdateRuleOutcome(CollectionChange change, CollectionRule before, string newName, string temp,
        IReadOnlyList<CollectionRule> rules, string collection)
    {
        var sameName = string.Equals(before.RuleName, newName, StringComparison.OrdinalIgnoreCase);
        var old = QueryRule(rules, before.RuleName) is { } r && string.Equals(r.Expression, before.Expression, StringComparison.Ordinal) ? r : null;
        var tempRule = QueryRule(rules, temp);
        var final = sameName
            ? old is null ? QueryRule(rules, newName) : null
            : QueryRule(rules, newName);
        var present = string.Join(", ", rules.Where(x => x.Kind == CollectionRuleKind.Query).Select(x => x.RuleName));
        var exists = $" Query rules on {collection} now: {Blank(present)}.";

        if (old is not null)
        {
            if (tempRule is not null)
                return Partial(change, $"ConfigMgr did not finish the update, so rule {before.RuleName} is not changed. The temporary rule {temp} with the new WQL also exists; Undo removes it." + exists,
                    RuleUndo(CollectionChangeKind.RemoveRule, change, tempRule, null));
            if (!sameName && final is not null)
                return Partial(change, $"Added rule {newName}, but ConfigMgr did not remove rule {before.RuleName}. Both rules exist now; Undo removes {newName}." + exists,
                    RuleUndo(CollectionChangeKind.RemoveRule, change, final, null));
            return Fail(change, $"ConfigMgr did not accept the new rule, so rule {before.RuleName} on {collection} is not changed. The log has the details.");
        }
        if (final is not null && tempRule is not null)
            return Partial(change, $"Rule {final.RuleName} has the new WQL, but ConfigMgr did not remove the temporary rule {temp}. Undo puts the old WQL back in {final.RuleName} and removes {temp}." + exists,
                RuleUndo(CollectionChangeKind.UpdateRule, change, final, before) with { Rules = [tempRule] });
        if (final is not null)
            return Partial(change, $"Rule {final.RuleName} has the new WQL, but ConfigMgr then reported an error." + exists,
                RuleUndo(CollectionChangeKind.UpdateRule, change, final, before));
        if (tempRule is not null)
            return Partial(change, $"ConfigMgr removed rule {before.RuleName} but did not add it again with the new WQL. The new WQL is in rule {temp}; Undo puts rule {before.RuleName} back." + exists,
                RuleUndo(CollectionChangeKind.UpdateRule, change, tempRule, before));
        return Fail(change, $"Rule {before.RuleName} on {collection} changed during the update. Refresh the list to see which rules exist." + exists);
    }

    // ---- Write runs ---------------------------------------------------------------------------

    /// <summary>
    /// The result of one write script run. <c>Uncertain</c> marks a failure that can follow a
    /// change ConfigMgr already made: the session failed during a write, or the script stopped
    /// after its cmdlet started.
    /// </summary>
    private sealed record WriteRun(WriteResult? Result, CollectionOutcome? Failure, bool Uncertain = false);

    private async Task<WriteRun> RunWriteAsync(CollectionChange change, ConfigMgrParameters parameters)
    {
        WriteResult? result;
        try
        {
            var output = await _session.InvokeAsync(WriteScript, Masked(parameters), CancellationToken.None).ConfigureAwait(false);
            result = Rows<WriteResult>(output, "Result").LastOrDefault();
        }
        catch (ConfigMgrSessionException ex)
        {
            return new WriteRun(null, Fail(change, WriteFailureMessage(ex, change)), true);
        }
        catch (CollectionServiceException)
        {
            result = null;
        }

        var target = Target(change);
        switch (result?.Status)
        {
            case "ok":
            case "rules-partial":
            case "update-failed":
                return new WriteRun(result, null);
            case "partial":
                LogDetail($"{change.Kind} failed after ConfigMgr started the change", result.Detail);
                return new WriteRun(result, Fail(change, $"ConfigMgr reported an error after it started the change to {target}. The log has the details."), true);
            case "collection-missing":
                return new WriteRun(result, Refused(change, CollectionMissingMessage(change.CollectionName, change.CollectionId)));
            case "built-in":
                return new WriteRun(result, Refused(change, BuiltInMessage(change)));
            case "name-taken":
                return new WriteRun(result, Refused(change, NameTakenMessage(change.AfterRow?.Name ?? "")));
            case "limiting-missing":
                return new WriteRun(result, Refused(change, $"The limiting collection {change.AfterRow?.LimitingCollectionId} does not exist any more. Select another limiting collection."));
            case "limiting-self":
                return new WriteRun(result, Refused(change, "A collection cannot be its own limiting collection."));
            case "changed":
                return new WriteRun(result, Refused(change, $"Collection {target} changed after the plan was shown, so it was not changed. Refresh the list and try again."));
            case "rule-taken":
                return new WriteRun(result, Refused(change, change.Kind == CollectionChangeKind.ApplyTemplate
                    ? $"Collection {target} already has a query rule named {change.RuleAfter?.RuleName}."
                    : $"Collection {target} already has this rule or a query rule with this name."));
            case "rule-missing":
                return new WriteRun(result, Refused(change, $"The rule {RuleOf(change)} does not exist on {target} any more. Refresh the list."));
            case "rule-changed":
                return new WriteRun(result, Refused(change, $"The rule {RuleOf(change)} on {target} changed after the plan was shown, so it was not changed. Refresh the list and try again."));
            case "rule-duplicate":
                return new WriteRun(result, Refused(change,
                    $"More than one query rule on {target} matches the name {RuleOf(change)}. The ConfigMgr cmdlet would remove all of them, so nothing was changed. Rename the rules in the console first."));
            case "target-missing":
                return new WriteRun(result, Refused(change, $"The device or collection that the rule refers to does not exist any more, or it is collection {target} itself."));
            case "is-limiting":
                return new WriteRun(result, Refused(change, $"Collection {target} is the limiting collection of {Count(result.References?.Length ?? 0, "collection")} ({string.Join(", ", result.References ?? [])}), so it is not removed."));
            case "is-referenced":
                return new WriteRun(result, Refused(change, $"Collection {target} is in an include or exclude rule of {Count(result.References?.Length ?? 0, "collection")} ({string.Join(", ", result.References ?? [])}). ConfigMgr does not remove it; remove those rules first."));
            case "query-missing":
                return new WriteRun(result, Refused(change, $"The saved query {QueryOf(change)} does not exist any more. Refresh the list."));
            case "query-built-in":
                return new WriteRun(result, Refused(change, $"The saved query {QueryOf(change)} is built in. It is not changed or deleted here."));
            case "query-changed":
                return new WriteRun(result, Refused(change, QueryChangedMessage(QueryOf(change))));
            case "query-name-taken":
                return new WriteRun(result, Refused(change, $"A saved query named {change.QueryAfter?.Name} exists already."));
            default:
                LogDetail($"{change.Kind} was not applied", result?.Detail ?? "The script returned no result.");
                return new WriteRun(result, Fail(change, $"ConfigMgr did not make the change to {target}. The log has the details."), result is null);
        }
    }

    /// <summary>
    /// Turns an uncertain failure into the change ConfigMgr holds now: a failed outcome that
    /// carries the undo when something changed, the original failure when nothing changed or
    /// when the state cannot be read.
    /// </summary>
    /// <param name="existed">
    /// For Create, Copy and SaveQuery, the IDs of the objects that had the new name before the
    /// write; null when they could not be read. Only an object that is new since then and holds
    /// the values of the change counts as made by this change.
    /// </param>
    private async Task<CollectionOutcome> ReconcileAsync(CollectionChange change, WriteRun run, IReadOnlySet<string>? existed = null)
    {
        if (!run.Uncertain || run.Failure is null) return run.Failure!;
        var target = Target(change);
        switch (change.Kind)
        {
            case CollectionChangeKind.Create:
            case CollectionChangeKind.Copy:
            {
                var (made, unclear) = await FindCreatedCollectionAsync(change, run.Result?.CreatedId, existed).ConfigureAwait(false);
                if (made is not null)
                    return Partial(change, $"ConfigMgr created collection {Shown(made.Row)} and then reported an error. The collection exists now; Undo removes it.", RemoveUndo(made));
                return unclear ? Fail(change, UnknownResultMessage(change)) : run.Failure;
            }
            case CollectionChangeKind.SetProperties when change.BeforeRow is { } before:
            {
                var now = await ReadBackAsync(change).ConfigureAwait(false);
                return now is not null && !SameProperties(now.Row, before)
                    ? Partial(change, $"ConfigMgr changed collection {Shown(now.Row)} and then reported an error. Refresh the list to see the stored values.", SetUndo(now.Row, before))
                    : run.Failure;
            }
            case CollectionChangeKind.AddRule:
            case CollectionChangeKind.ApplyTemplate:
            {
                var now = await ReadBackAsync(change).ConfigureAwait(false);
                return now is not null && change.RuleAfter is { } rule && FindRule(now.Rules, rule) is { } added
                    ? Partial(change, $"ConfigMgr added {RuleText(added)} to {target} and then reported an error. The rule exists now; Undo removes it.",
                        RuleUndo(CollectionChangeKind.RemoveRule, change, added, null))
                    : run.Failure;
            }
            case CollectionChangeKind.RemoveRule:
            {
                var now = await ReadBackAsync(change).ConfigureAwait(false);
                return now is not null && change.RuleBefore is { } rule && FindRule(now.Rules, rule) is null
                    ? Partial(change, $"ConfigMgr removed {RuleText(rule)} from {target} and then reported an error. Undo adds the rule again.",
                        RuleUndo(CollectionChangeKind.AddRule, change, null, rule))
                    : run.Failure;
            }
            case CollectionChangeKind.Remove when change.BeforeRow is { } before && change.Rules is { } rules:
            {
                var now = await ReadStateOrNullAsync(change.CollectionId).ConfigureAwait(false);
                return now.Read && now.State is null
                    ? Partial(change, $"ConfigMgr removed collection {Shown(before)} and then reported an error. {RemoveUndoNote}", CreateUndo(before, rules))
                    : run.Failure;
            }
            case CollectionChangeKind.SaveQuery when change.QueryAfter is { } query:
            {
                var (made, unclear) = await FindCreatedQueryAsync(query, run.Result?.CreatedId, existed).ConfigureAwait(false);
                if (made is not null)
                    return Partial(change, $"ConfigMgr saved query {made.Name} ({made.QueryId}) and then reported an error. Undo deletes it.", QueryUndo(CollectionChangeKind.DeleteQuery, made, null));
                return unclear ? Fail(change, UnknownResultMessage(change)) : run.Failure;
            }
            case CollectionChangeKind.UpdateQuery when change.QueryBefore is { } before:
            {
                var (read, now) = await ReadQueryOrNullAsync(before.QueryId).ConfigureAwait(false);
                return read && now is not null && !SameQuery(now, before)
                    ? Partial(change, $"ConfigMgr changed saved query {now.Name} and then reported an error. Undo sets the old values back.", QueryUndo(CollectionChangeKind.UpdateQuery, now, before))
                    : run.Failure;
            }
            case CollectionChangeKind.DeleteQuery when change.QueryBefore is { } before:
            {
                var (read, now) = await ReadQueryOrNullAsync(before.QueryId).ConfigureAwait(false);
                return read && now is null
                    ? Partial(change, $"ConfigMgr deleted saved query {before.Name} and then reported an error. Undo saves it again with a new query ID.", QueryUndo(CollectionChangeKind.SaveQuery, null, before))
                    : run.Failure;
            }
            default:
                return run.Failure;
        }
    }

    private CollectionOutcome Partial(CollectionChange change, string message, CollectionChange? undo)
    {
        _log?.Warning(LogArea, $"{change.Kind} did not finish after ConfigMgr made a change; the outcome carries its undo.");
        return new CollectionOutcome(change, false, message, undo);
    }

    private string WriteFailureMessage(ConfigMgrSessionException ex, CollectionChange change)
    {
        var target = Target(change);
        _log?.Warning(LogArea, $"A change failed in the ConfigMgr session: {ex.Failure}, {FailureKinds(ex.Message)}.");
        return ex.Failure switch
        {
            ConfigMgrFailure.AccessDenied => $"ConfigMgr denied the change to {target}. {AccessHint}",
            ConfigMgrFailure.Timeout => $"The result of the change to {target} is not known: ConfigMgr did not answer in time. Refresh the list to see the current state.",
            ConfigMgrFailure.ProcessDied => $"The result of the change to {target} is not known: the ConfigMgr session ended during the change. Refresh the list to see the current state.",
            ConfigMgrFailure.Unexpected => $"The result of the change to {target} is not known: the ConfigMgr session failed. Refresh the list to see the current state.",
            ConfigMgrFailure.ScriptFailed => $"ConfigMgr reported an error for {target}. The log has the details.",
            _ => $"The change to {target} was not made. {SessionProblem(ex.Failure)}"
        };
    }

    /// <summary>Fixed text for a session failure that happens before any command runs.</summary>
    private static string SessionProblem(ConfigMgrFailure failure) => failure switch
    {
        ConfigMgrFailure.AccessDenied => $"The SMS Provider denied access. {AccessHint}",
        ConfigMgrFailure.InvalidSiteCode => $"The site code is not valid. Correct it in {ConfigMgrSessionHost.SettingsLocation}.",
        ConfigMgrFailure.PowerShellNotStarted => "Windows PowerShell 5.1 (powershell.exe) could not start. ConfigMgr commands need it.",
        ConfigMgrFailure.ModuleNotFound => "The ConfigurationManager PowerShell module was not found. Install the Configuration Manager console on this computer.",
        ConfigMgrFailure.DriveNotConnected => $"The connection to the ConfigMgr site failed. Check the site code and the SMS Provider in {ConfigMgrSessionHost.SettingsLocation}.",
        ConfigMgrFailure.Timeout => "ConfigMgr did not answer in time. Try again.",
        ConfigMgrFailure.ProcessDied => "The Windows PowerShell session for ConfigMgr ended. Try again; the next command starts a new session.",
        ConfigMgrFailure.RequestTooLarge => "The values are too large for one ConfigMgr command.",
        _ => "The ConfigMgr session failed. The log has the details."
    };

    // ---- Undo builders ------------------------------------------------------------------------

    /// <summary>A collection this change made has no deployments; the Undo is refused when one was added since.</summary>
    private static CollectionChange RemoveUndo(CollectionState made) =>
        new(CollectionChangeKind.Remove, made.Row.CollectionId, made.Row.Name, made.Row, null, null, null, null, null,
            [$"Removes collection {Shown(made.Row)}.", "If a deployment or a maintenance window was added to it since, it is not removed."], made.Rules,
            new CollectionRemoveCounts(-1, 0, made.WindowCount));

    private static CollectionChange CreateUndo(CollectionRow removed, IReadOnlyList<CollectionRule> rules) =>
        new(CollectionChangeKind.Create, "", removed.Name, null, removed with { CollectionId = "" }, null, null, null, null,
            [$"Creates collection {removed.Name} again with {Count(rules.Count, "rule")}. The new collection has a new ID. Deployments and maintenance windows are not restored."], rules);

    private static CollectionChange SetUndo(CollectionRow now, CollectionRow old) =>
        new(CollectionChangeKind.SetProperties, now.CollectionId, now.Name, now, old with { CollectionId = now.CollectionId }, null, null, null, null,
            [$"Sets the old properties of collection {Shown(now)} back."]);

    private static CollectionChange RuleUndo(CollectionChangeKind kind, CollectionChange change, CollectionRule? before, CollectionRule? after) =>
        new(kind, change.CollectionId, change.CollectionName, null, null, before, after, null, null, []);

    private static CollectionChange QueryUndo(CollectionChangeKind kind, SavedQuery? before, SavedQuery? after) =>
        new(kind, "", "", null, null, null, null, before, after, []);

    // ---- Helpers ------------------------------------------------------------------------------

    private ConfigMgrParameters Expected(CollectionChange change, CollectionRow before) => new()
    {
        { "CollectionId", change.CollectionId },
        { "CheckState", true },
        { "ExpectedName", before.Name },
        { "ExpectedComment", before.Comment ?? "" },
        { "ExpectedLimitingId", before.LimitingCollectionId ?? "" },
        { "ExpectedRefreshType", RefreshValue(before.RefreshType) },
        { "ExpectedRefreshSchedule", before.RefreshSchedule ?? "" }
    };

    /// <summary>
    /// A captured schedule token goes to the script as it is, so an Undo puts the exact schedule
    /// back; otherwise the interval of the row, or <paramref name="fallback"/>, is built anew.
    /// </summary>
    private static void AddSchedule(ConfigMgrParameters parameters, CollectionRow row, RefreshInterval? fallback)
    {
        var token = ScheduleOrEmpty(row.RefreshSchedule);
        if (token.Length > 0)
        {
            parameters["RefreshSchedule"] = token;
            return;
        }
        if ((row.RefreshInterval ?? fallback) is { } interval && RefreshInterval.Check(interval) is null)
        {
            parameters["RefreshUnit"] = interval.Unit;
            parameters["RefreshCount"] = interval.Count;
        }
    }

    private static ConfigMgrParameters RuleParameters(string operation, string collectionId, CollectionRule rule)
    {
        var parameters = new ConfigMgrParameters
        {
            { "Operation", operation }, { "CollectionId", collectionId }, { "RuleKind", rule.Kind.ToString() }
        };
        if (rule.Kind == CollectionRuleKind.Direct)
            parameters["ResourceId"] = long.TryParse(rule.ResourceId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0L;
        if (rule.Kind is CollectionRuleKind.Include or CollectionRuleKind.Exclude)
            parameters["IncludedCollectionId"] = rule.IncludedCollectionId ?? "";
        return parameters;
    }

    private static ConfigMgrParameters QueryParameters(string operation, SavedQuery query, SavedQuery? expected)
    {
        var parameters = new ConfigMgrParameters
        {
            { "Operation", operation }, { "QueryId", query.QueryId ?? "" }, { "Name", query.Name?.Trim() ?? "" },
            { "Expression", query.Expression ?? "" }, { "Comment", query.Comment ?? "" },
            { "TargetClassName", query.TargetClassName ?? "" }, { "LimitToCollectionId", query.LimitToCollectionId ?? "" }
        };
        if (expected is not null)
        {
            parameters["ExpectedName"] = expected.Name ?? "";
            parameters["ExpectedExpression"] = expected.Expression ?? "";
            parameters["ExpectedComment"] = expected.Comment ?? "";
            parameters["ExpectedTargetClassName"] = expected.TargetClassName ?? "";
            parameters["ExpectedLimitToCollectionId"] = expected.LimitToCollectionId ?? "";
        }
        return parameters;
    }

    /// <summary>The key of a rule as the write script builds it for the state check of a remove.</summary>
    internal static string RuleKey(CollectionRule rule) => rule.Kind switch
    {
        CollectionRuleKind.Direct => "Direct|" + rule.ResourceId,
        CollectionRuleKind.Query => "Query|" + rule.RuleName + "|" + rule.Expression,
        _ => rule.Kind + "|" + (rule.IncludedCollectionId ?? "").ToUpperInvariant()
    };

    private static CollectionRule? QueryRule(IEnumerable<CollectionRule> rules, string? name) =>
        rules.FirstOrDefault(r => r.Kind == CollectionRuleKind.Query && string.Equals(r.RuleName, name, StringComparison.OrdinalIgnoreCase));

    private static CollectionRule? FindRule(IEnumerable<CollectionRule> rules, CollectionRule rule) => rule.Kind switch
    {
        CollectionRuleKind.Query => QueryRule(rules, rule.RuleName),
        CollectionRuleKind.Direct => rules.FirstOrDefault(r => r.Kind == CollectionRuleKind.Direct && r.ResourceId == rule.ResourceId),
        _ => rules.FirstOrDefault(r => r.Kind == rule.Kind && string.Equals(r.IncludedCollectionId, rule.IncludedCollectionId, StringComparison.OrdinalIgnoreCase))
    };

    private static bool SameProperties(CollectionRow a, CollectionRow b) =>
        string.Equals(a.Name, b.Name, StringComparison.Ordinal) && string.Equals(a.Comment, b.Comment, StringComparison.Ordinal)
        && string.Equals(a.LimitingCollectionId, b.LimitingCollectionId, StringComparison.OrdinalIgnoreCase)
        && a.RefreshType == b.RefreshType && string.Equals(a.RefreshSchedule, b.RefreshSchedule, StringComparison.OrdinalIgnoreCase);

    private static bool SameQuery(SavedQuery a, SavedQuery b) =>
        string.Equals(a.Name, b.Name, StringComparison.Ordinal) && string.Equals(a.Expression, b.Expression, StringComparison.Ordinal)
        && string.Equals(a.Comment ?? "", b.Comment ?? "", StringComparison.Ordinal)
        && string.Equals(a.TargetClassName ?? "", b.TargetClassName ?? "", StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.LimitToCollectionId ?? "", b.LimitToCollectionId ?? "", StringComparison.OrdinalIgnoreCase);

    private static string CheckedCollectionId(string? id)
    {
        var trimmed = id?.Trim() ?? "";
        if (!IsCollectionId(trimmed)) throw new CollectionServiceException("Enter a collection ID of one to eight letters and digits, for example SMS00001.");
        return trimmed.ToUpperInvariant();
    }

    private static string? CheckName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Enter a collection name.";
        if (name.Trim().Length > MaxNameLength) return $"The name is longer than {MaxNameLength} characters.";
        if (name.Any(char.IsControl)) return "The name contains a control character.";
        return null;
    }

    private static string CheckedName(string? name, string emptyMessage)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new CollectionServiceException(emptyMessage);
        if (CheckName(name) is { } reason) throw new CollectionServiceException(reason);
        return name.Trim();
    }

    private static string? InvalidCreate(CollectionRow? row, IReadOnlyList<CollectionRule>? rules)
    {
        if (row is null) return null;
        if (CheckName(row.Name) is { } reason) return reason;
        if (!IsCollectionId(row.LimitingCollectionId)) return "Select a limiting collection.";
        if (!IsRefreshType(row.RefreshType)) return "Select the refresh type: Manual, Periodic, Continuous or Both.";
        if (RefreshInterval.Check(row.RefreshInterval) is { } interval) return interval;
        return rules?.Select(InvalidRule).FirstOrDefault(r => r is not null);
    }

    private static string? InvalidRule(CollectionRule? rule)
    {
        if (rule is null) return null;
        switch (rule.Kind)
        {
            case CollectionRuleKind.Query:
                return WqlGuard.CheckRuleName(rule.RuleName) ?? WqlGuard.Check(rule.Expression);
            case CollectionRuleKind.Direct:
                return long.TryParse(rule.ResourceId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
                    ? null : "Select the device to add as a direct member.";
            default:
                return IsCollectionId(rule.IncludedCollectionId) ? null : "Select the collection to include or exclude.";
        }
    }

    private static CollectionRule CheckedRule(CollectionRule rule)
    {
        if (InvalidRule(rule) is { } reason) throw new CollectionServiceException(reason);
        return rule.Kind switch
        {
            CollectionRuleKind.Query => rule with { RuleName = rule.RuleName.Trim(), ResourceId = "", IncludedCollectionId = "" },
            CollectionRuleKind.Direct => rule with { Expression = "", IncludedCollectionId = "" },
            _ => rule with { Expression = "", ResourceId = "", IncludedCollectionId = rule.IncludedCollectionId.ToUpperInvariant() }
        };
    }

    private static string? InvalidQuery(SavedQuery? query)
    {
        if (query is null) return null;
        if (string.IsNullOrWhiteSpace(query.Name)) return "Enter a name for the saved query.";
        if (query.Name.Trim().Length > MaxNameLength) return $"The name is longer than {MaxNameLength} characters.";
        if (query.Name.Any(char.IsControl)) return "The name contains a control character.";
        if (WqlGuard.Check(query.Expression) is { } reason) return reason;
        if (!string.IsNullOrEmpty(query.LimitToCollectionId) && !IsCollectionId(query.LimitToCollectionId)) return "The limiting collection of the query is not a collection ID.";
        if (!string.IsNullOrEmpty(query.TargetClassName) && !Regex.IsMatch(query.TargetClassName, "^[A-Za-z_][A-Za-z0-9_]{0,127}$"))
            return "The target class name is not a WMI class name.";
        return null;
    }

    private static SavedQuery CheckedQuery(SavedQuery? query)
    {
        if (query is null) throw new CollectionServiceException("The change has no saved query values.");
        if (InvalidQuery(query) is { } reason) throw new CollectionServiceException(reason);
        return query with
        {
            Name = query.Name.Trim(), Comment = query.Comment ?? "",
            TargetClassName = string.IsNullOrWhiteSpace(query.TargetClassName) ? "SMS_R_System" : query.TargetClassName,
            LimitToCollectionId = (query.LimitToCollectionId ?? "").ToUpperInvariant()
        };
    }

    private static bool IsRefreshType(string? type) => type is "Manual" or "Periodic" or "Continuous" or "Both";

    private static string CheckedRefreshType(string? type) =>
        IsRefreshType(type) ? type! : throw new CollectionServiceException("Select the refresh type: Manual, Periodic, Continuous or Both.");

    private static string ScheduleOrEmpty(string? token) => token is not null && ScheduleRegex().IsMatch(token) ? token.ToUpperInvariant() : "";

    /// <summary>The RefreshType of SMS_Collection: 1 manual, 2 periodic, 4 continuous (incremental), 6 both.</summary>
    internal static string RefreshName(int value) => value switch
    {
        1 => "Manual",
        2 => "Periodic",
        4 => "Continuous",
        6 => "Both",
        _ => $"Unknown ({value})"
    };

    internal static int RefreshValue(string? name) => name switch
    {
        "Manual" => 1,
        "Periodic" => 2,
        "Continuous" => 4,
        "Both" => 6,
        _ => int.TryParse(new string((name ?? "").Where(char.IsDigit).ToArray()), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0
    };

    private static string RefreshText(string type) => type switch
    {
        "Manual" => "manual only",
        "Periodic" => "on a schedule",
        "Continuous" => "incremental updates",
        "Both" => "on a schedule and incremental updates",
        _ => type
    };

    private static CollectionRow ToRow(ReadRow r)
    {
        var id = r.CollectionId ?? "";
        return new CollectionRow(id, r.Name ?? "", MaintenanceWindowService.FolderPath(r.ObjectPath), r.LimitToCollectionId ?? "", r.LimitToCollectionName ?? "",
            r.MemberCount, IsBuiltIn(id), RefreshName(r.RefreshType), r.Comment ?? "", r.DirectRuleCount, r.QueryRuleCount, r.IncludeRuleCount, r.ExcludeRuleCount,
            ScheduleOrEmpty(r.RefreshSchedule), IntervalOf(ScheduleOrEmpty(r.RefreshSchedule)));
    }

    private static CollectionRule? ToRule(RuleRow r)
    {
        if (!Enum.TryParse<CollectionRuleKind>(r.RuleKind, ignoreCase: false, out var kind)) return null;
        var detail = kind switch
        {
            CollectionRuleKind.Direct => $"Resource {r.ResourceId}",
            CollectionRuleKind.Include or CollectionRuleKind.Exclude => $"Collection {r.IncludedCollectionId}",
            _ => ""
        };
        return new CollectionRule(kind, r.RuleName ?? "", r.Expression ?? "", r.ResourceId ?? "", r.IncludedCollectionId ?? "", detail);
    }

    private static SavedQuery ToQuery(QueryRow q) =>
        new(q.QueryId ?? "", q.Name ?? "", q.Expression ?? "", q.TargetClassName ?? "", q.Comment ?? "", q.LimitToCollectionId ?? "");

    private CollectionState? StateOf(WriteResult result) =>
        result.Collection is { } row
            ? new CollectionState(ToRow(row), (result.Rules ?? []).Select(ToRule).OfType<CollectionRule>().ToList(), row.WindowCount)
            : null;

    private IReadOnlyList<T> Rows<T>(ConfigMgrCommandResult result, string kind)
    {
        try
        {
            return result.Output
                .Where(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("Kind", out var k) && k.ValueKind == JsonValueKind.String && k.GetString() == kind)
                .Select(e => e.Deserialize<T>(ReadOptions)!)
                .ToList();
        }
        catch (JsonException ex)
        {
            _log?.Warning(LogArea, $"ConfigMgr returned output that could not be read ({ex.GetType().Name}).");
            throw new CollectionServiceException(UnreadableMessage);
        }
    }

    private const string UnreadableMessage = "ConfigMgr returned data that could not be read. The log has the details.";

    private static readonly HashSet<string> LoggedParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        "Operation", "CollectionId", "LimitingCollectionId", "RefreshType", "RefreshSchedule", "RefreshUnit",
        "QueryId", "IncludedCollectionId", "LimitToCollectionId", "RuleKind", "TargetClassName"
    };

    /// <summary>
    /// The session logs every parameter that is not secret and masks secret values in the error
    /// text it returns, so names, comments and WQL travel as secret values.
    /// </summary>
    private static ConfigMgrParameters Masked(ConfigMgrParameters parameters)
    {
        var masked = new ConfigMgrParameters();
        foreach (var (name, value) in parameters)
        {
            if (value is string text && !LoggedParameters.Contains(name)) masked.AddSecret(name, text);
            else masked.Add(name, value);
        }
        return masked;
    }

    private async Task<ConfigMgrCommandResult> ReadAsync(ConfigMgrParameters parameters, CancellationToken ct)
    {
        try
        {
            return await _session.InvokeAsync(ReadScript, Masked(parameters), ct).ConfigureAwait(false);
        }
        catch (ConfigMgrSessionException ex)
        {
            _log?.Warning(LogArea, $"Reading collections failed in the ConfigMgr session: {ex.Failure}, {FailureKinds(ex.Message)}.");
            throw new CollectionServiceException(ex.Failure == ConfigMgrFailure.ScriptFailed
                ? "The collections could not be read from ConfigMgr. The log has the details."
                : SessionProblem(ex.Failure));
        }
    }

    private async Task<CollectionState?> ReadStateAsync(string? collectionId, CancellationToken ct)
    {
        var id = CheckedCollectionId(collectionId);
        var output = await ReadAsync(new ConfigMgrParameters { { "Operation", "Get" }, { "CollectionId", id } }, ct).ConfigureAwait(false);
        return ParseState(output);
    }

    private CollectionState? ParseState(ConfigMgrCommandResult output)
    {
        var row = Rows<ReadRow>(output, "Collection").FirstOrDefault();
        if (row is null) return null;
        var rules = Rows<RuleRow>(output, "Rule").Select(ToRule).OfType<CollectionRule>().ToList();
        return new CollectionState(ToRow(row), rules, row.WindowCount);
    }

    private async Task<CollectionState> RequireStateAsync(CollectionChange request, CancellationToken ct) =>
        await ReadStateAsync(request.CollectionId, ct).ConfigureAwait(false)
            ?? throw new CollectionServiceException(CollectionMissingMessage(request.CollectionName, request.CollectionId));

    /// <summary>Reads a collection after an uncertain failure; <c>Read</c> is false when the read fails too.</summary>
    private async Task<(bool Read, CollectionState? State)> ReadStateOrNullAsync(string collectionId)
    {
        try
        {
            return (true, await ReadStateAsync(collectionId, CancellationToken.None).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is CollectionServiceException)
        {
            _log?.Warning(LogArea, "A change failed and the collection could not be read back to find what changed.");
            return (false, null);
        }
    }

    private async Task<CollectionState?> ReadBackAsync(CollectionChange change) =>
        IsCollectionId(change.CollectionId) ? (await ReadStateOrNullAsync(change.CollectionId).ConfigureAwait(false)).State : null;

    /// <summary>The IDs of the collections named <paramref name="name"/>; null when they cannot be read.</summary>
    private async Task<IReadOnlySet<string>?> CollectionIdsNamedAsync(string name)
    {
        try
        {
            var output = await ReadAsync(new ConfigMgrParameters { { "Operation", "FindCollection" }, { "Name", name } }, CancellationToken.None).ConfigureAwait(false);
            return Rows<FoundRow>(output, "Found").Select(f => f.CollectionId ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (CollectionServiceException)
        {
            return null;
        }
    }

    /// <summary>The IDs of the saved queries named <paramref name="name"/>; null when they cannot be read.</summary>
    private async Task<IReadOnlySet<string>?> QueryIdsNamedAsync(string name)
    {
        try
        {
            var output = await ReadAsync(new ConfigMgrParameters { { "Operation", "FindQuery" }, { "Name", name } }, CancellationToken.None).ConfigureAwait(false);
            return Rows<QueryRow>(output, "SavedQuery").Select(q => q.QueryId ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (CollectionServiceException)
        {
            return null;
        }
    }

    private async Task<CollectionState?> ReadCreatedAsync(string? id) =>
        IsCollectionId(id) ? (await ReadStateOrNullAsync(id!).ConfigureAwait(false)).State : null;

    private async Task<SavedQuery?> ReadCreatedQueryAsync(string? id) =>
        id is not null && QueryIdRegex().IsMatch(id) ? (await ReadQueryOrNullAsync(id).ConfigureAwait(false)).Query : null;

    /// <summary>
    /// The collection a Create or Copy made: the one with the ID the script returned, or else the
    /// only collection with the new name that did not exist before the write and holds the
    /// limiting collection and comment of the change. <c>Unclear</c> is true when such a
    /// collection may exist but cannot be shown to come from this change.
    /// </summary>
    private async Task<(CollectionState? Made, bool Unclear)> FindCreatedCollectionAsync(CollectionChange change, string? createdId, IReadOnlySet<string>? existed)
    {
        if (IsCollectionId(createdId))
        {
            var (read, state) = await ReadStateOrNullAsync(createdId!).ConfigureAwait(false);
            return (state, !read);
        }
        var after = change.AfterRow;
        var now = await CollectionIdsNamedAsync(after?.Name?.Trim() ?? "").ConfigureAwait(false);
        if (now is null) return (null, true);
        var fresh = now.Where(id => existed is null || !existed.Contains(id)).ToList();
        if (fresh.Count == 0) return (null, false);
        if (existed is null || fresh.Count > 1 || after is null) return (null, true);
        var made = await ReadCreatedAsync(fresh[0]).ConfigureAwait(false);
        var same = made is not null
            && string.Equals(made.Row.LimitingCollectionId, after.LimitingCollectionId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(made.Row.Comment, after.Comment ?? "", StringComparison.Ordinal);
        return same ? (made, false) : (null, true);
    }

    private async Task<(SavedQuery? Made, bool Unclear)> FindCreatedQueryAsync(SavedQuery query, string? createdId, IReadOnlySet<string>? existed)
    {
        if (createdId is not null && QueryIdRegex().IsMatch(createdId))
        {
            var (read, made) = await ReadQueryOrNullAsync(createdId).ConfigureAwait(false);
            return (made, !read);
        }
        var now = await QueryIdsNamedAsync(query.Name.Trim()).ConfigureAwait(false);
        if (now is null) return (null, true);
        var fresh = now.Where(id => existed is null || !existed.Contains(id)).ToList();
        if (fresh.Count == 0) return (null, false);
        if (existed is null || fresh.Count > 1) return (null, true);
        var candidate = await ReadCreatedQueryAsync(fresh[0]).ConfigureAwait(false);
        var same = candidate is not null
            && string.Equals(candidate.Expression, query.Expression, StringComparison.Ordinal)
            && string.Equals(candidate.Comment ?? "", query.Comment ?? "", StringComparison.Ordinal);
        return same ? (candidate, false) : (null, true);
    }

    private static string UnknownResultMessage(CollectionChange change)
    {
        var (thing, name) = change.Kind == CollectionChangeKind.SaveQuery
            ? ("saved query", change.QueryAfter?.Name?.Trim() ?? "")
            : ("collection", change.AfterRow?.Name?.Trim() ?? "");
        return $"The result of the change to {Target(change)} is not known. ConfigMgr reported an error, and this change cannot be shown to have made the {thing} named {name}. Check in ConfigMgr whether {thing} {name} exists and who made it. Undo is not available.";
    }

    private static WriteRun Unread(CollectionChange change, WriteRun run) =>
        run with { Failure = Fail(change, NotReadBackMessage(change)), Uncertain = true };

    private async Task CheckNameFreeAsync(string name, CancellationToken ct)
    {
        var output = await ReadAsync(new ConfigMgrParameters { { "Operation", "FindCollection" }, { "Name", name } }, ct).ConfigureAwait(false);
        if (Rows<FoundRow>(output, "Found").Count > 0) throw new CollectionServiceException(NameTakenMessage(name));
    }

    private async Task<SavedQuery> RequireQueryAsync(SavedQuery? query, CancellationToken ct)
    {
        if (query is null || !QueryIdRegex().IsMatch(query.QueryId ?? "")) throw new CollectionServiceException("Select a saved query.");
        if (IsBuiltInQuery(query.QueryId)) throw new CollectionServiceException(BuiltInQueryMessage(query));
        var output = await ReadAsync(new ConfigMgrParameters { { "Operation", "Query" }, { "QueryId", query.QueryId } }, ct).ConfigureAwait(false);
        return Rows<QueryRow>(output, "SavedQuery").Select(ToQuery).FirstOrDefault()
            ?? throw new CollectionServiceException($"The saved query {query.Name} does not exist any more. Refresh the list.");
    }

    private async Task CheckQueryNameFreeAsync(string name, string? exceptId, CancellationToken ct)
    {
        var output = await ReadAsync(new ConfigMgrParameters { { "Operation", "FindQuery" }, { "Name", name } }, ct).ConfigureAwait(false);
        if (Rows<QueryRow>(output, "SavedQuery").Any(q => !string.Equals(q.QueryId, exceptId, StringComparison.OrdinalIgnoreCase)))
            throw new CollectionServiceException($"A saved query named {name} exists already.");
    }

    private async Task<(bool Read, SavedQuery? Query)> ReadQueryOrNullAsync(string queryId)
    {
        try
        {
            var output = await ReadAsync(new ConfigMgrParameters { { "Operation", "Query" }, { "QueryId", queryId } }, CancellationToken.None).ConfigureAwait(false);
            return (true, Rows<QueryRow>(output, "SavedQuery").Select(ToQuery).FirstOrDefault());
        }
        catch (Exception ex) when (ex is CollectionServiceException)
        {
            return (false, null);
        }
    }

    /// <summary>The change with its collection ID trimmed and in upper case, as ConfigMgr compares IDs.</summary>
    private static CollectionChange Normalized(CollectionChange change)
    {
        var id = (change.CollectionId ?? "").Trim().ToUpperInvariant();
        return id == change.CollectionId ? change : change with { CollectionId = id };
    }

    /// <summary>Refuses a change whose Undo would have to add <paramref name="rule"/> again when the Undo would refuse that rule.</summary>
    private static void RefuseUnrestorable(CollectionRow row, CollectionRule rule, string action)
    {
        if (InvalidRule(rule) is { } reason)
            throw new CollectionServiceException(
                $"Undo could not add the {RuleText(rule)} of collection {Shown(row)} again, so it is not {action}. {reason}");
    }

    private static void RefuseUnrestorable(SavedQuery query, string action)
    {
        if (InvalidQuery(query) is { } reason)
            throw new CollectionServiceException(
                $"Undo could not save the query {Shown(query.Name, query.QueryId)} again with its current values, so it is not {action}. {reason}");
    }

    private static void RefuseBuiltIn(CollectionChange request)
    {
        if (IsBuiltIn(request.CollectionId)) throw new CollectionServiceException(BuiltInMessage(request));
    }

    /// <summary>Provider error text can hold rule names, WQL and device names, so only these parts of it reach the log.</summary>
    private void LogDetail(string what, string? detail)
    {
        if (!string.IsNullOrWhiteSpace(detail))
            _log?.Warning(LogArea, $"{what}: {FailureKinds(detail)}.");
    }

    [GeneratedRegex(@"\b(?:[A-Za-z_][A-Za-z0-9_]*\.)*[A-Za-z_][A-Za-z0-9_]*Exception\b|\b0x[0-9A-Fa-f]{8}\b")]
    private static partial Regex FailureKindRegex();

    /// <summary>The exception type names and HRESULT codes in <paramref name="text"/>, without the rest of it.</summary>
    internal static string FailureKinds(string? text)
    {
        var kinds = FailureKindRegex().Matches(text ?? "").Select(m => m.Value).Distinct(StringComparer.Ordinal).Take(5).ToList();
        return kinds.Count == 0 ? "no error type given" : string.Join(", ", kinds);
    }

    private static CollectionOutcome Fail(CollectionChange change, string message) => new(change, false, message, null);

    /// <summary>An outcome for a change that was not attempted: refused, stale or not prepared.</summary>
    private static CollectionOutcome Refused(CollectionChange change, string message) =>
        new(change, false, message.StartsWith(SkippedPrefix, StringComparison.Ordinal) ? message : SkippedPrefix + message, null);

    private static CollectionOutcome Done(CollectionChange change, string message, CollectionChange? undo) => new(change, true, message, undo);

    private const string NotPreparedMessage = "The change has no captured state, so it was not applied. Refresh the list and try again.";

    private static string NotReadBackMessage(CollectionChange change) =>
        $"ConfigMgr reported the change to {Target(change)} as done, but the result could not be read back. Refresh the list to see the current state.";

    private static string Target(CollectionChange change) => change.Kind switch
    {
        CollectionChangeKind.SaveQuery or CollectionChangeKind.UpdateQuery or CollectionChangeKind.DeleteQuery => "saved query " + QueryOf(change),
        CollectionChangeKind.Create => "collection " + (change.AfterRow?.Name ?? change.CollectionName),
        _ => "collection " + Shown(change.CollectionName, change.CollectionId)
    };

    private static string QueryOf(CollectionChange change) => change.QueryBefore?.Name ?? change.QueryAfter?.Name ?? "";

    private static string RuleOf(CollectionChange change) => (change.RuleBefore ?? change.RuleAfter) is { } r ? RuleText(r) : "";

    private static string RuleText(CollectionRule rule) => rule.Kind switch
    {
        CollectionRuleKind.Query => $"query rule {rule.RuleName}",
        CollectionRuleKind.Direct => $"direct rule for {Shown(rule.RuleName, rule.ResourceId)}",
        CollectionRuleKind.Include => $"include rule for {Shown(rule.RuleName, rule.IncludedCollectionId)}",
        _ => $"exclude rule for {Shown(rule.RuleName, rule.IncludedCollectionId)}"
    };

    private static string KindWord(CollectionRuleKind kind) => kind == CollectionRuleKind.Include ? "include" : "exclude";

    private static string MembershipLine(CollectionRuleKind kind) => kind == CollectionRuleKind.Include
        ? "An include rule changes membership evaluation: the members of the included collection become members of this collection."
        : "An exclude rule changes membership evaluation: the members of the excluded collection are taken out of this collection.";

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n.ToString(CultureInfo.InvariantCulture)} {noun}s";

    private static string Blank(string? text) => string.IsNullOrEmpty(text) ? "(empty)" : text;

    private static string BuiltInMessage(CollectionChange change) =>
        $"{Shown(change.CollectionName, change.CollectionId)} is a built-in collection. Built-in collections are not changed or removed here.";

    private static string BuiltInQueryMessage(SavedQuery query) =>
        $"The saved query {Shown(query.Name, query.QueryId)} is built in. It is not changed or deleted here.";

    private static string CollectionMissingMessage(string? name, string id) =>
        $"The collection {Shown(name, id)} does not exist. Refresh the list.";

    private static string NameTakenMessage(string name) => $"A collection named {name} exists already. Collection names are unique in the site.";

    private static string RuleMissingMessage(CollectionRow row, CollectionRule rule) => $"The {RuleText(rule)} does not exist on collection {Shown(row)} any more. Refresh the list.";

    private static string RuleChangedMessage(CollectionRow row, CollectionRule rule) =>
        $"The {RuleText(rule)} on collection {Shown(row)} changed since it was read. Refresh the list and try again.";

    private static string RuleTakenMessage(CollectionRow row, string name) => $"Collection {Shown(row)} already has a query rule named {name}.";

    private static string QueryChangedMessage(string name) =>
        $"The saved query {name} changed after it was read, so it was not changed. Refresh the list and try again.";

    private static string Shown(CollectionRow row) => Shown(row.Name, row.CollectionId);

    private static string Shown(string? name, string id) => string.IsNullOrWhiteSpace(name) ? id : string.IsNullOrWhiteSpace(id) ? name : $"{name} ({id})";
}
