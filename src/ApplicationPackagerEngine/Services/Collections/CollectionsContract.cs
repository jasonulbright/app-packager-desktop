namespace ApplicationPackagerEngine.Services;

public enum CollectionRuleKind { Direct, Query, Include, Exclude }

/// <param name="FolderPath">The console folder path, for example "Servers\Production"; empty at the root.</param>
/// <param name="IsBuiltIn">True for a collection whose ID starts with SMS: it cannot be removed or edited here.</param>
/// <param name="RefreshType">Manual, Periodic, Continuous or Both.</param>
/// <param name="RefreshSchedule">The ConfigMgr schedule token of the periodic refresh; empty when the collection has none.</param>
/// <param name="RefreshInterval">The periodic refresh interval to set; null keeps the current schedule.</param>
public sealed record CollectionRow(
    string CollectionId,
    string Name,
    string FolderPath,
    string LimitingCollectionId,
    string LimitingCollectionName,
    int MemberCount,
    bool IsBuiltIn,
    string RefreshType,
    string Comment,
    int DirectRuleCount,
    int QueryRuleCount,
    int IncludeRuleCount,
    int ExcludeRuleCount,
    string RefreshSchedule = "",
    RefreshInterval? RefreshInterval = null);

/// <param name="Unit">Minutes, Hours or Days.</param>
public sealed record RefreshInterval(int Count, string Unit);

/// <param name="Expression">The WQL of a query rule; empty for the other kinds.</param>
/// <param name="ResourceId">The resource of a direct rule; empty for the other kinds.</param>
/// <param name="IncludedCollectionId">The collection of an include or exclude rule; empty for the other kinds.</param>
public sealed record CollectionRule(
    CollectionRuleKind Kind,
    string RuleName,
    string Expression,
    string ResourceId,
    string IncludedCollectionId,
    string Detail);

public sealed record CollectionMember(string ResourceId, string Name, string ClientState, bool IsDirect);

/// <param name="Members">One page of members only.</param>
/// <param name="MemberTotal">The number of members of the whole collection.</param>
public sealed record CollectionDetail(
    CollectionRow Row,
    IReadOnlyList<CollectionRule> Rules,
    IReadOnlyList<CollectionMember> Members,
    int MemberTotal);

public sealed record WqlResult(
    bool IsValid,
    string Message,
    int MatchCount,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string>> FirstRows);

/// <param name="LimitToCollectionId">The collection the query is limited to; empty when it is not limited.</param>
public sealed record SavedQuery(string QueryId, string Name, string Expression, string TargetClassName, string Comment, string LimitToCollectionId = "");

/// <param name="Parameters">The placeholders of a parameterized template as they appear in <paramref name="Expression"/>, for example "{SoftwareName}".</param>
/// <param name="IsShipped">True for a template shipped with this product; false for a user template.</param>
/// <param name="ParameterDetails">The label, help text and default value of each placeholder, in the order of <paramref name="Parameters"/>.</param>
/// <param name="LimitingCollectionName">The limiting collection the template names; empty when it names none.</param>
public sealed record QueryTemplate(
    string Id,
    string Name,
    string Category,
    string Expression,
    IReadOnlyList<string> Parameters,
    string Description,
    bool IsShipped,
    IReadOnlyList<QueryTemplateParameter>? ParameterDetails = null,
    string LimitingCollectionName = "");

public sealed record QueryTemplateParameter(string Placeholder, string Label, string HelpText, string DefaultValue);

public enum CollectionChangeKind
{
    Create,
    Copy,
    SetProperties,
    AddRule,
    UpdateRule,
    RemoveRule,
    Evaluate,
    Remove,
    SaveQuery,
    UpdateQuery,
    DeleteQuery,
    ApplyTemplate
}

/// <summary>
/// One change. What each kind reads:
/// Create creates <see cref="AfterRow"/> (its id is ignored) with the rules in <see cref="Rules"/>,
/// or with <see cref="RuleAfter"/> when no list is given.
/// Copy copies the collection <see cref="CollectionId"/> under the name of <see cref="AfterRow"/>.
/// SetProperties sets name, comment, limiting collection and refresh type of <see cref="AfterRow"/>
/// on the collection captured in <see cref="BeforeRow"/>.
/// AddRule and ApplyTemplate add <see cref="RuleAfter"/>; UpdateRule replaces <see cref="RuleBefore"/>
/// with <see cref="RuleAfter"/>; RemoveRule removes <see cref="RuleBefore"/>.
/// Evaluate starts a membership update. Remove removes the collection <see cref="BeforeRow"/>;
/// after preparation <see cref="Rules"/> holds its rules, which the Undo creates again.
/// SaveQuery creates <see cref="QueryAfter"/>; UpdateQuery sets <see cref="QueryAfter"/> on
/// <see cref="QueryBefore"/>; DeleteQuery removes <see cref="QueryBefore"/>.
/// </summary>
/// <param name="Impact">
/// Plain lines for the plan. <see cref="ICollectionService.PrepareAsync"/> keeps the lines of the
/// request (for example the match count of the last preview) and adds its own.
/// </param>
/// <param name="Rules">All rules of the collection for Create and Remove; null for the other kinds.</param>
public sealed record CollectionChange(
    CollectionChangeKind Kind,
    string CollectionId,
    string CollectionName,
    CollectionRow? BeforeRow,
    CollectionRow? AfterRow,
    CollectionRule? RuleBefore,
    CollectionRule? RuleAfter,
    SavedQuery? QueryBefore,
    SavedQuery? QueryAfter,
    IReadOnlyList<string> Impact,
    IReadOnlyList<CollectionRule>? Rules = null);

/// <param name="Message">Plain text for the operator; never raw exception text.</param>
/// <param name="UndoChange">The change that reverses this one; set on every outcome that changed ConfigMgr and can be reversed.</param>
public sealed record CollectionOutcome(CollectionChange Change, bool Succeeded, string Message, CollectionChange? UndoChange);

public interface ICollectionService
{
    /// <summary>The device collections, built-in ones included and flagged.</summary>
    Task<IReadOnlyList<CollectionRow>> ListCollectionsAsync(CancellationToken ct);

    /// <summary>One collection by ID without the list; null when it does not exist.</summary>
    Task<CollectionRow?> GetCollectionAsync(string collectionId, CancellationToken ct);

    Task<CollectionDetail> GetDetailAsync(string collectionId, int memberPage, CancellationToken ct);

    Task<WqlResult> ValidateWqlAsync(string expression, CancellationToken ct);

    Task<WqlResult> PreviewWqlAsync(string expression, int maxRows, CancellationToken ct);

    Task<IReadOnlyList<SavedQuery>> ListSavedQueriesAsync(CancellationToken ct);

    /// <summary>
    /// Reads the current state and returns the change with the Before values and the Impact
    /// lines filled. Throws <see cref="CollectionServiceException"/> with a plain message when
    /// the change cannot be made in the current state.
    /// </summary>
    Task<CollectionChange> PrepareAsync(CollectionChange request, CancellationToken ct);

    /// <summary>
    /// Applies the changes in order and reports each outcome through <paramref name="progress"/>
    /// as soon as it is known. Cancellation stops between changes; the changes not started are
    /// returned as skipped outcomes.
    /// </summary>
    Task<IReadOnlyList<CollectionOutcome>> ApplyAsync(
        IReadOnlyList<CollectionChange> changes,
        IProgress<CollectionOutcome> progress,
        CancellationToken ct);
}

public interface IQueryTemplateStore
{
    /// <summary>The shipped templates first, then the user templates.</summary>
    IReadOnlyList<QueryTemplate> All();

    /// <summary>A plain message about user template files that were skipped; null when there is none.</summary>
    string? Message { get; }
}

public interface ICollectionReportWriter
{
    /// <summary>Writes the rows to a new CSV file and returns its path.</summary>
    string WriteCsv(IReadOnlyList<CollectionRow> rows);

    /// <summary>Writes the rows to a new HTML file and returns its path.</summary>
    string WriteHtml(IReadOnlyList<CollectionRow> rows);
}

/// <summary>A collection operation that could not run; the message is plain text for the operator.</summary>
public sealed class CollectionServiceException(string message) : Exception(message);
