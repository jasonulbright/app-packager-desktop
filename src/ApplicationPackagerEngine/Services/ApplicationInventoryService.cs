using System.Data;
using System.Data.SQLite;
using System.Globalization;
using ApplicationPackagerEngine.Models.Inventory;
using ApplicationPackagerEngine.Models.Workbench;
using ApplicationPackagerEngine.Services.Workbench;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// The durable operational inventory shared by desktop and command-line callers.
/// Each mutation is one SQLite transaction and one short-lived connection so writes
/// from separate processes serialize without replacing another process's state.
/// </summary>
public sealed class ApplicationInventoryService
{
    public const string LegacySettingsMigrationId = "settings-appstates-v1";
    private readonly string _dbPath;

    public ApplicationInventoryService(string? dbPath = null)
    {
        _dbPath = Path.GetFullPath(dbPath ?? Path.Combine(AppDataPaths.LegacyRoot, "history.db"));
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        InitializeSchema();
    }

    public void RegisterIdentity(InventoryApplicationIdentity identity)
    {
        ValidateIdentity(identity, requireProfile: true);
        Mutate((connection, transaction) =>
        {
            using var command = Command(connection, transaction, """
                INSERT INTO inventory_identities(packager_uid, application_id, profile_id, profile_revision, updated_at)
                VALUES($uid, $appId, $profileId, $revision, $at)
                ON CONFLICT(packager_uid, application_id, profile_id) DO UPDATE SET
                    profile_revision = COALESCE(excluded.profile_revision, inventory_identities.profile_revision),
                    updated_at = excluded.updated_at
                """);
            Add(command, "$uid", identity.PackagerUid);
            Add(command, "$appId", identity.ApplicationId);
            Add(command, "$profileId", identity.ProfileId);
            Add(command, "$revision", identity.ProfileRevision);
            Add(command, "$at", Stamp(DateTimeOffset.UtcNow));
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public void BeginOperation(InventoryOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        Required(operation.OperationId, nameof(operation.OperationId));
        Required(operation.Kind, nameof(operation.Kind));
        Required(operation.PackagerUid, nameof(operation.PackagerUid));
        if (operation.Application is not null) ValidateIdentity(operation.Application);
        Mutate((connection, transaction) =>
        {
            using var command = Command(connection, transaction, """
                INSERT INTO inventory_operations(operation_id, kind, packager_uid, application_id, profile_id, target,
                    started_at, finished_at, outcome, detail)
                VALUES($id, $kind, $uid, $appId, $profileId, $target, $started, NULL, $outcome, NULL)
                """);
            Add(command, "$id", operation.OperationId);
            Add(command, "$kind", operation.Kind);
            Add(command, "$uid", operation.PackagerUid);
            Add(command, "$appId", operation.Application?.ApplicationId);
            Add(command, "$profileId", operation.Application?.ProfileId);
            Add(command, "$target", operation.Target);
            Add(command, "$started", Stamp(operation.StartedAt));
            Add(command, "$outcome", InventoryOutcome.Unknown.ToString());
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public void CompleteOperation(string operationId, InventoryOutcome outcome, DateTimeOffset finishedAt, string? detail = null)
    {
        Required(operationId, nameof(operationId));
        Mutate((connection, transaction) =>
        {
            using var command = Command(connection, transaction, """
                UPDATE inventory_operations SET finished_at = $finished, outcome = $outcome, detail = $detail
                WHERE operation_id = $id AND finished_at IS NULL
                """);
            Add(command, "$finished", Stamp(finishedAt));
            Add(command, "$outcome", outcome.ToString());
            Add(command, "$detail", detail);
            Add(command, "$id", operationId);
            if (command.ExecuteNonQuery() != 1)
                throw new InvalidOperationException($"Operation '{operationId}' was not running or was already completed.");
            return 0;
        });
    }

    public void RecordVendorCheck(InventoryVendorCheck check)
    {
        ArgumentNullException.ThrowIfNull(check);
        Required(check.PackagerUid, nameof(check.PackagerUid));
        Required(check.AppName, nameof(check.AppName));
        Required(check.Source, nameof(check.Source));
        if (check.Outcome == InventoryOutcome.Succeeded && string.IsNullOrWhiteSpace(check.Version))
            throw new ArgumentException("A successful vendor check must include a version.", nameof(check));
        var success = check.Outcome == InventoryOutcome.Succeeded && !string.IsNullOrWhiteSpace(check.Version);
        Mutate((connection, transaction) =>
        {
            InsertEvent(connection, transaction, new InventoryEvent(
                "VendorCheck", check.PackagerUid, check.AppName, null, null, null, null,
                check.Version, check.Source, check.Outcome, null, null, null, check.At, check.OperationId, check.Detail));
            using var command = Command(connection, transaction, """
                INSERT INTO vendor_inventory(packager_uid, app_name, latest_version, latest_source, last_succeeded_at,
                    last_attempt_at, last_attempt_outcome, last_attempt_source, last_attempt_detail)
                VALUES($uid, $name, $latestVersion, $latestSource, $successAt, $attemptAt, $outcome, $source, $detail)
                ON CONFLICT(packager_uid) DO UPDATE SET
                    app_name = excluded.app_name,
                    latest_version = CASE WHEN $success = 1 AND (vendor_inventory.last_succeeded_at IS NULL OR excluded.last_succeeded_at >= vendor_inventory.last_succeeded_at) THEN excluded.latest_version ELSE vendor_inventory.latest_version END,
                    latest_source = CASE WHEN $success = 1 AND (vendor_inventory.last_succeeded_at IS NULL OR excluded.last_succeeded_at >= vendor_inventory.last_succeeded_at) THEN excluded.latest_source ELSE vendor_inventory.latest_source END,
                    last_succeeded_at = CASE WHEN $success = 1 AND (vendor_inventory.last_succeeded_at IS NULL OR excluded.last_succeeded_at >= vendor_inventory.last_succeeded_at) THEN excluded.last_succeeded_at ELSE vendor_inventory.last_succeeded_at END,
                    last_attempt_at = CASE WHEN vendor_inventory.last_attempt_at IS NULL OR excluded.last_attempt_at >= vendor_inventory.last_attempt_at THEN excluded.last_attempt_at ELSE vendor_inventory.last_attempt_at END,
                    last_attempt_outcome = CASE WHEN vendor_inventory.last_attempt_at IS NULL OR excluded.last_attempt_at >= vendor_inventory.last_attempt_at THEN excluded.last_attempt_outcome ELSE vendor_inventory.last_attempt_outcome END,
                    last_attempt_source = CASE WHEN vendor_inventory.last_attempt_at IS NULL OR excluded.last_attempt_at >= vendor_inventory.last_attempt_at THEN excluded.last_attempt_source ELSE vendor_inventory.last_attempt_source END,
                    last_attempt_detail = CASE WHEN vendor_inventory.last_attempt_at IS NULL OR excluded.last_attempt_at >= vendor_inventory.last_attempt_at THEN excluded.last_attempt_detail ELSE vendor_inventory.last_attempt_detail END
                """);
            Add(command, "$uid", check.PackagerUid);
            Add(command, "$name", check.AppName);
            Add(command, "$latestVersion", success ? check.Version : null);
            Add(command, "$latestSource", success ? check.Source : null);
            Add(command, "$successAt", success ? Stamp(check.At) : null);
            Add(command, "$attemptAt", Stamp(check.At));
            Add(command, "$outcome", check.Outcome.ToString());
            Add(command, "$source", check.Source);
            Add(command, "$detail", check.Detail);
            Add(command, "$success", success ? 1 : 0);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public InventoryVendorState? GetVendorState(string packagerUid)
    {
        Required(packagerUid, nameof(packagerUid));
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT latest_version, latest_source, last_succeeded_at, last_attempt_at, last_attempt_outcome, last_attempt_source, last_attempt_detail FROM vendor_inventory WHERE packager_uid = $uid";
        Add(command, "$uid", packagerUid);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new InventoryVendorState(packagerUid, Text(reader, 0), Text(reader, 1), MaybeTime(reader, 2),
            MaybeTime(reader, 3), ParseOutcome(reader.GetString(4)), reader.GetString(5), Text(reader, 6));
    }

    public void RecordEnvironmentObservation(InventoryEnvironmentObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ValidateEnvironmentObservation(observation.PackagerUid, observation.Environment, observation.Application);
        Required(observation.Source, nameof(observation.Source));
        var completed = observation.Outcome is InventoryOutcome.Succeeded or InventoryOutcome.NotFound;
        Mutate((connection, transaction) =>
        {
            if (observation.Application is not null) RegisterIdentity(connection, transaction, observation.Application, observation.At);
            InsertEvent(connection, transaction, new InventoryEvent(
                "EnvironmentObservation", observation.PackagerUid, null, observation.Application?.ApplicationId,
                observation.Application?.ProfileId, observation.Environment.Destination, observation.Environment.Key,
                observation.Version, observation.Source, observation.Outcome, null, null, null, observation.At,
                observation.OperationId, observation.Detail));
            using var command = Command(connection, transaction, """
                INSERT INTO environment_inventory(packager_uid, destination, environment_key, application_id, profile_id,
                    latest_observed_version, latest_observation_source, latest_observed_at, last_attempt_at,
                    last_attempt_outcome, last_attempt_source, last_attempt_detail)
                VALUES($uid, $destination, $environment, $appId, $profileId, $observedVersion, $observedSource,
                    $observedAt, $attemptAt, $outcome, $source, $detail)
                ON CONFLICT(packager_uid, destination, environment_key) DO UPDATE SET
                    application_id = COALESCE(excluded.application_id, environment_inventory.application_id),
                    profile_id = COALESCE(excluded.profile_id, environment_inventory.profile_id),
                    latest_observation_source = CASE WHEN $completed = 1 AND (environment_inventory.latest_observed_at IS NULL OR excluded.latest_observed_at >= environment_inventory.latest_observed_at) THEN excluded.latest_observation_source ELSE environment_inventory.latest_observation_source END,
                    latest_observed_at = CASE WHEN $completed = 1 AND (environment_inventory.latest_observed_at IS NULL OR excluded.latest_observed_at >= environment_inventory.latest_observed_at) THEN excluded.latest_observed_at ELSE environment_inventory.latest_observed_at END,
                    latest_observed_version = CASE WHEN $completed = 1 AND (environment_inventory.latest_observed_at IS NULL OR excluded.latest_observed_at >= environment_inventory.latest_observed_at) THEN excluded.latest_observed_version ELSE environment_inventory.latest_observed_version END,
                    last_attempt_at = CASE WHEN environment_inventory.last_attempt_at IS NULL OR excluded.last_attempt_at >= environment_inventory.last_attempt_at THEN excluded.last_attempt_at ELSE environment_inventory.last_attempt_at END,
                    last_attempt_outcome = CASE WHEN environment_inventory.last_attempt_at IS NULL OR excluded.last_attempt_at >= environment_inventory.last_attempt_at THEN excluded.last_attempt_outcome ELSE environment_inventory.last_attempt_outcome END,
                    last_attempt_source = CASE WHEN environment_inventory.last_attempt_at IS NULL OR excluded.last_attempt_at >= environment_inventory.last_attempt_at THEN excluded.last_attempt_source ELSE environment_inventory.last_attempt_source END,
                    last_attempt_detail = CASE WHEN environment_inventory.last_attempt_at IS NULL OR excluded.last_attempt_at >= environment_inventory.last_attempt_at THEN excluded.last_attempt_detail ELSE environment_inventory.last_attempt_detail END
                """);
            Add(command, "$uid", observation.PackagerUid);
            Add(command, "$destination", observation.Environment.Destination);
            Add(command, "$environment", observation.Environment.Key);
            Add(command, "$appId", observation.Application?.ApplicationId);
            Add(command, "$profileId", observation.Application?.ProfileId);
            Add(command, "$observedVersion", completed ? observation.Version : null);
            Add(command, "$observedSource", completed ? observation.Source : null);
            Add(command, "$observedAt", completed ? Stamp(observation.At) : null);
            Add(command, "$attemptAt", Stamp(observation.At));
            Add(command, "$outcome", observation.Outcome.ToString());
            Add(command, "$source", observation.Source);
            Add(command, "$detail", observation.Detail);
            Add(command, "$completed", completed ? 1 : 0);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public InventoryEnvironmentState? GetEnvironmentState(string packagerUid, InventoryEnvironmentIdentity environment)
    {
        ValidateEnvironment(environment);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT application_id, profile_id, latest_observed_version, latest_observation_source, latest_observed_at, last_attempt_at, last_attempt_outcome, last_attempt_source, last_attempt_detail FROM environment_inventory WHERE packager_uid = $uid AND destination = $destination AND environment_key = $environment";
        Add(command, "$uid", packagerUid);
        Add(command, "$destination", environment.Destination);
        Add(command, "$environment", environment.Key);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new InventoryEnvironmentState(packagerUid, environment.Destination, environment.Key, Text(reader, 0), Text(reader, 1),
            Text(reader, 2), Text(reader, 3), MaybeTime(reader, 4), MaybeTime(reader, 5),
            ParseOutcome(reader.GetString(6)), reader.GetString(7), Text(reader, 8));
    }

    public void RecordStagedBuild(InventoryStagedBuild build)
    {
        ArgumentNullException.ThrowIfNull(build);
        ValidateIdentity(build.Application, requireProfile: true);
        Required(build.Version, nameof(build.Version));
        Required(build.BuildId, nameof(build.BuildId));
        Required(build.BuildRecordPath, nameof(build.BuildRecordPath));
        Required(build.ManifestPath, nameof(build.ManifestPath));
        Mutate((connection, transaction) =>
        {
            RegisterIdentity(connection, transaction, build.Application, build.StagedAt);
            InsertEvent(connection, transaction, new InventoryEvent(
                "Stage", build.Application.PackagerUid, null, build.Application.ApplicationId, build.Application.ProfileId,
                null, null, build.Version, "Workbench", InventoryOutcome.Succeeded, null, build.BuildId,
                build.BuildRecordPath, build.StagedAt, build.OperationId, Detail: null, Action: "Stage", ManifestPath: build.ManifestPath));
            using var command = Command(connection, transaction, """
                INSERT INTO package_inventory(application_id, profile_id, packager_uid, profile_revision, version,
                    build_id, build_record_path, manifest_path, staged_at)
                VALUES($appId, $profileId, $uid, $revision, $version, $buildId, $recordPath, $manifestPath, $at)
                ON CONFLICT(application_id, profile_id) DO UPDATE SET
                    packager_uid = excluded.packager_uid,
                    profile_revision = excluded.profile_revision,
                    version = excluded.version,
                    build_id = excluded.build_id,
                    build_record_path = excluded.build_record_path,
                    manifest_path = excluded.manifest_path,
                    staged_at = excluded.staged_at
                WHERE excluded.staged_at >= package_inventory.staged_at
                """);
            Add(command, "$appId", build.Application.ApplicationId);
            Add(command, "$profileId", build.Application.ProfileId);
            Add(command, "$uid", build.Application.PackagerUid);
            Add(command, "$revision", build.Application.ProfileRevision);
            Add(command, "$version", build.Version);
            Add(command, "$buildId", build.BuildId);
            Add(command, "$recordPath", build.BuildRecordPath);
            Add(command, "$manifestPath", build.ManifestPath);
            Add(command, "$at", Stamp(build.StagedAt));
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public InventoryPackageState? GetPackageState(string applicationId, string profileId)
    {
        Required(applicationId, nameof(applicationId));
        Required(profileId, nameof(profileId));
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT packager_uid, profile_revision, version, build_id, build_record_path, manifest_path, staged_at FROM package_inventory WHERE application_id = $appId AND profile_id = $profileId";
        Add(command, "$appId", applicationId);
        Add(command, "$profileId", profileId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new InventoryPackageState(applicationId, profileId, reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetInt32(1),
            reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), ParseTime(reader.GetString(6)));
    }

    public void RecordDestinationOutcome(InventoryDestinationOutcome result)
    {
        ArgumentNullException.ThrowIfNull(result);
        ValidateEnvironmentObservation(result.PackagerUid, result.Environment, result.Application);
        Required(result.Action, nameof(result.Action));
        Required(result.Version, nameof(result.Version));
        var succeeded = result.Outcome is InventoryOutcome.Succeeded or InventoryOutcome.AlreadyPresent;
        Mutate((connection, transaction) =>
        {
            if (result.Application is not null) RegisterIdentity(connection, transaction, result.Application, result.At);
            InsertEvent(connection, transaction, new InventoryEvent(
                result.Action, result.PackagerUid, null, result.Application?.ApplicationId, result.Application?.ProfileId,
                result.Environment.Destination, result.Environment.Key, result.Version, null, result.Outcome,
                result.ObjectId, result.BuildId, null, result.At, result.OperationId, result.Detail, Action: result.Action));
            using var command = Command(connection, transaction, """
                INSERT INTO destination_inventory(packager_uid, destination, environment_key, action, application_id,
                    profile_id, last_attempt_version, last_attempt_outcome, last_attempt_object_id, last_attempt_at,
                    last_success_version, last_success_object_id, last_success_at, last_success_build_id, last_attempt_detail)
                VALUES($uid, $destination, $environment, $action, $appId, $profileId, $attemptVersion, $outcome,
                    $attemptObject, $attemptAt, $successVersion, $successObject, $successAt, $successBuildId, $detail)
                ON CONFLICT(packager_uid, destination, environment_key, action) DO UPDATE SET
                    application_id = COALESCE(excluded.application_id, destination_inventory.application_id),
                    profile_id = COALESCE(excluded.profile_id, destination_inventory.profile_id),
                    last_attempt_version = CASE WHEN destination_inventory.last_attempt_at IS NULL OR excluded.last_attempt_at >= destination_inventory.last_attempt_at THEN excluded.last_attempt_version ELSE destination_inventory.last_attempt_version END,
                    last_attempt_outcome = CASE WHEN destination_inventory.last_attempt_at IS NULL OR excluded.last_attempt_at >= destination_inventory.last_attempt_at THEN excluded.last_attempt_outcome ELSE destination_inventory.last_attempt_outcome END,
                    last_attempt_object_id = CASE WHEN destination_inventory.last_attempt_at IS NULL OR excluded.last_attempt_at >= destination_inventory.last_attempt_at THEN excluded.last_attempt_object_id ELSE destination_inventory.last_attempt_object_id END,
                    last_attempt_at = CASE WHEN destination_inventory.last_attempt_at IS NULL OR excluded.last_attempt_at >= destination_inventory.last_attempt_at THEN excluded.last_attempt_at ELSE destination_inventory.last_attempt_at END,
                    last_success_version = CASE WHEN $succeeded = 1 AND (destination_inventory.last_success_at IS NULL OR excluded.last_success_at >= destination_inventory.last_success_at) THEN excluded.last_success_version ELSE destination_inventory.last_success_version END,
                    last_success_object_id = CASE WHEN $succeeded = 1 AND (destination_inventory.last_success_at IS NULL OR excluded.last_success_at >= destination_inventory.last_success_at) THEN excluded.last_success_object_id ELSE destination_inventory.last_success_object_id END,
                    last_success_at = CASE WHEN $succeeded = 1 AND (destination_inventory.last_success_at IS NULL OR excluded.last_success_at >= destination_inventory.last_success_at) THEN excluded.last_success_at ELSE destination_inventory.last_success_at END,
                    last_success_build_id = CASE WHEN $succeeded = 1 AND (destination_inventory.last_success_at IS NULL OR excluded.last_success_at >= destination_inventory.last_success_at) THEN excluded.last_success_build_id ELSE destination_inventory.last_success_build_id END,
                    last_attempt_detail = CASE WHEN destination_inventory.last_attempt_at IS NULL OR excluded.last_attempt_at >= destination_inventory.last_attempt_at THEN excluded.last_attempt_detail ELSE destination_inventory.last_attempt_detail END
                """);
            Add(command, "$uid", result.PackagerUid);
            Add(command, "$destination", result.Environment.Destination);
            Add(command, "$environment", result.Environment.Key);
            Add(command, "$action", result.Action);
            Add(command, "$appId", result.Application?.ApplicationId);
            Add(command, "$profileId", result.Application?.ProfileId);
            Add(command, "$attemptVersion", result.Version);
            Add(command, "$outcome", result.Outcome.ToString());
            Add(command, "$attemptObject", result.ObjectId);
            Add(command, "$attemptAt", Stamp(result.At));
            Add(command, "$successVersion", succeeded ? result.Version : null);
            Add(command, "$successObject", succeeded ? result.ObjectId : null);
            Add(command, "$successAt", succeeded ? Stamp(result.At) : null);
            Add(command, "$successBuildId", succeeded ? result.BuildId : null);
            Add(command, "$detail", result.Detail);
            Add(command, "$succeeded", succeeded ? 1 : 0);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public InventoryDestinationState? GetDestinationState(string packagerUid, InventoryEnvironmentIdentity environment, string action)
    {
        ValidateEnvironment(environment);
        Required(action, nameof(action));
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT application_id, profile_id, last_attempt_version, last_attempt_outcome, last_attempt_object_id, last_attempt_at, last_success_version, last_success_object_id, last_success_at, last_success_build_id, last_attempt_detail FROM destination_inventory WHERE packager_uid = $uid AND destination = $destination AND environment_key = $environment AND action = $action";
        Add(command, "$uid", packagerUid);
        Add(command, "$destination", environment.Destination);
        Add(command, "$environment", environment.Key);
        Add(command, "$action", action);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new InventoryDestinationState(packagerUid, environment.Destination, environment.Key, action, Text(reader, 0), Text(reader, 1),
            Text(reader, 2), ParseOutcome(reader.GetString(3)), Text(reader, 4), MaybeTime(reader, 5), Text(reader, 6),
            Text(reader, 7), MaybeTime(reader, 8), Text(reader, 9), Text(reader, 10));
    }

    public InventoryRefusalState? GetLatestRefusal(string packagerUid, InventoryEnvironmentIdentity environment, string action)
    {
        Required(packagerUid, nameof(packagerUid));
        ValidateEnvironment(environment);
        Required(action, nameof(action));
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT version, detail, occurred_at FROM inventory_events WHERE packager_uid = $uid AND destination = $destination AND environment_key = $environment AND action = $action AND outcome = $outcome ORDER BY occurred_at DESC, id DESC LIMIT 1";
        Add(command, "$uid", packagerUid);
        Add(command, "$destination", environment.Destination);
        Add(command, "$environment", environment.Key);
        Add(command, "$action", action);
        Add(command, "$outcome", InventoryOutcome.Refused.ToString());
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new InventoryRefusalState(Text(reader, 0) ?? "", Text(reader, 1), MaybeTime(reader, 2))
            : null;
    }

    public InventoryOperationState? GetOperation(string operationId)
    {
        Required(operationId, nameof(operationId));
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT kind, packager_uid, application_id, profile_id, target, started_at, finished_at, outcome, detail FROM inventory_operations WHERE operation_id = $id";
        Add(command, "$id", operationId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadOperation(operationId, reader) : null;
    }

    /// <summary>Completed and unfinished operations, newest first, for a history view or report.</summary>
    public IReadOnlyList<InventoryOperationState> GetOperations(string packagerUid, int limit = 100)
    {
        Required(packagerUid, nameof(packagerUid));
        if (limit is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(limit));
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT operation_id, kind, packager_uid, application_id, profile_id, target, started_at, finished_at, outcome, detail FROM inventory_operations WHERE packager_uid = $uid ORDER BY started_at DESC, operation_id DESC LIMIT $limit";
        Add(command, "$uid", packagerUid);
        Add(command, "$limit", limit);
        using var reader = command.ExecuteReader();
        var operations = new List<InventoryOperationState>();
        while (reader.Read()) operations.Add(ReadOperation(reader.GetString(0), reader, 1));
        return operations;
    }

    /// <summary>Operations left running when a process stopped before recording a result.</summary>
    public IReadOnlyList<InventoryOperationState> GetUnfinishedOperations()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT operation_id, kind, packager_uid, application_id, profile_id, target, started_at, finished_at, outcome, detail FROM inventory_operations WHERE finished_at IS NULL ORDER BY started_at";
        using var reader = command.ExecuteReader();
        var operations = new List<InventoryOperationState>();
        while (reader.Read()) operations.Add(ReadOperation(reader.GetString(0), reader, 1));
        return operations;
    }

    /// <summary>Persisted facts, newest first, for a history view or report.</summary>
    public IReadOnlyList<InventoryEventRecord> GetEvents(string packagerUid, int limit = 100)
    {
        Required(packagerUid, nameof(packagerUid));
        if (limit is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(limit));
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, event_id, kind, packager_uid, app_name, application_id, profile_id, destination, environment_key, action, version, source, outcome, object_id, build_id, build_record_path, manifest_path, occurred_at, operation_id, detail FROM inventory_events WHERE packager_uid = $uid ORDER BY id DESC LIMIT $limit";
        Add(command, "$uid", packagerUid);
        Add(command, "$limit", limit);
        return ReadEvents(command);
    }

    public IReadOnlyList<InventoryEventRecord> GetEventsForOperation(string operationId)
    {
        Required(operationId, nameof(operationId));
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, event_id, kind, packager_uid, app_name, application_id, profile_id, destination, environment_key, action, version, source, outcome, object_id, build_id, build_record_path, manifest_path, occurred_at, operation_id, detail FROM inventory_events WHERE operation_id = $id ORDER BY id";
        Add(command, "$id", operationId);
        return ReadEvents(command);
    }

    /// <summary>
    /// Imports settings.json operational state exactly once. Old destination records had
    /// no environment key, so they are retained under the explicit "unknown" key.
    /// </summary>
    public bool ImportLegacyAppStates(IEnumerable<LegacyInventoryAppState> states, DateTimeOffset importedAt,
        string migrationId = LegacySettingsMigrationId)
    {
        ArgumentNullException.ThrowIfNull(states);
        Required(migrationId, nameof(migrationId));
        var snapshot = states.ToArray();
        if (snapshot.Any(state => string.IsNullOrWhiteSpace(state.PackagerUid)))
            throw new ArgumentException("Every imported state must have a packager UID.", nameof(states));
        return Mutate((connection, transaction) =>
        {
            using (var check = Command(connection, transaction, "SELECT 1 FROM inventory_migrations WHERE migration_id = $id"))
            {
                Add(check, "$id", migrationId);
                if (check.ExecuteScalar() is not null) return false;
            }

            foreach (var state in snapshot) ImportLegacyState(connection, transaction, state);
            using var mark = Command(connection, transaction, "INSERT INTO inventory_migrations(migration_id, completed_at) VALUES($id, $at)");
            Add(mark, "$id", migrationId);
            Add(mark, "$at", Stamp(importedAt));
            mark.ExecuteNonQuery();
            return true;
        });
    }

    private void ImportLegacyState(SQLiteConnection connection, SQLiteTransaction transaction, LegacyInventoryAppState state)
    {
        var uid = state.PackagerUid.Trim();
        var latest = string.IsNullOrWhiteSpace(state.LatestVersion) ? null : state.LatestVersion;
        using (var command = Command(connection, transaction, """
            INSERT OR IGNORE INTO vendor_inventory(packager_uid, app_name, latest_version, latest_source, last_succeeded_at,
                last_attempt_at, last_attempt_outcome, last_attempt_source, last_attempt_detail)
            VALUES($uid, $name, $version, $source, $checked, $checked, $outcome, $source, NULL)
            """))
        {
            Add(command, "$uid", uid);
            Add(command, "$name", uid);
            Add(command, "$version", latest);
            Add(command, "$source", "legacy-settings");
            Add(command, "$checked", latest is not null && state.LastChecked is { } checkedAt ? Stamp(checkedAt) : null);
            Add(command, "$outcome", InventoryOutcome.LegacyImported.ToString());
            command.ExecuteNonQuery();
        }
        InsertEvent(connection, transaction, new InventoryEvent("LegacyVendorState", uid, uid, null, null, null, null,
            latest, "legacy-settings", InventoryOutcome.LegacyImported, null, null, null, state.LastChecked, null,
            "settings.json AppStates"));

        if (!string.IsNullOrWhiteSpace(state.CurrentEnvironmentVersion))
        {
            using var command = Command(connection, transaction, """
                INSERT OR IGNORE INTO environment_inventory(packager_uid, destination, environment_key, application_id, profile_id,
                    latest_observed_version, latest_observation_source, latest_observed_at, last_attempt_at,
                    last_attempt_outcome, last_attempt_source, last_attempt_detail)
                VALUES($uid, 'ConfigMgr', 'unknown', NULL, NULL, $version, 'legacy-settings', NULL, NULL, $outcome, 'legacy-settings', NULL)
                """);
            Add(command, "$uid", uid);
            Add(command, "$version", state.CurrentEnvironmentVersion);
            Add(command, "$outcome", InventoryOutcome.LegacyImported.ToString());
            command.ExecuteNonQuery();
            InsertEvent(connection, transaction, new InventoryEvent("LegacyEnvironmentObservation", uid, uid, null, null,
                "ConfigMgr", "unknown", state.CurrentEnvironmentVersion, "legacy-settings", InventoryOutcome.LegacyImported,
                null, null, null, null, null, "Original ConfigMgr site was not saved with the value."));
        }

        if (state.LastStaged is { } stagedAt)
            InsertEvent(connection, transaction, new InventoryEvent("LegacyStage", uid, uid, null, null, null, null,
                null, "legacy-settings", InventoryOutcome.LegacyImported, null, null, null, stagedAt, null,
                "Stage time retained; the old state had no version, profile, build ID or manifest reference."));

        foreach (var group in state.Destinations.GroupBy(fact => (Destination: fact.Destination.Trim(), Action: "Publish"), StringTupleComparer.Instance))
        {
            if (group.Key.Destination.Length == 0) continue;
            var facts = group.ToArray();
            foreach (var fact in facts)
                InsertEvent(connection, transaction, new InventoryEvent("LegacyDestinationOutcome", uid, uid, null, null,
                    group.Key.Destination, "unknown", fact.Version, "legacy-settings", fact.Outcome, fact.ObjectId, null,
                    null, fact.At, null, fact.Detail ?? "Original destination environment was not saved.", Action: group.Key.Action));
            var lastAttempt = facts.OrderBy(fact => fact.At ?? DateTimeOffset.MinValue).Last();
            var lastSuccess = facts.Where(fact => fact.Outcome is InventoryOutcome.Succeeded or InventoryOutcome.AlreadyPresent)
                .OrderBy(fact => fact.At ?? DateTimeOffset.MinValue).LastOrDefault();
            using var command = Command(connection, transaction, """
                INSERT OR IGNORE INTO destination_inventory(packager_uid, destination, environment_key, action, application_id,
                    profile_id, last_attempt_version, last_attempt_outcome, last_attempt_object_id, last_attempt_at,
                    last_success_version, last_success_object_id, last_success_at, last_success_build_id, last_attempt_detail)
                VALUES($uid, $destination, 'unknown', $action, NULL, NULL, $attemptVersion, $outcome, $attemptObject, $attemptAt,
                    $successVersion, $successObject, $successAt, NULL, $detail)
                """);
            Add(command, "$uid", uid);
            Add(command, "$destination", group.Key.Destination);
            Add(command, "$action", group.Key.Action);
            Add(command, "$attemptVersion", lastAttempt.Version);
            Add(command, "$outcome", InventoryOutcome.LegacyImported.ToString());
            Add(command, "$attemptObject", lastAttempt.ObjectId);
            Add(command, "$attemptAt", lastAttempt.At is { } attemptAt ? Stamp(attemptAt) : null);
            Add(command, "$successVersion", lastSuccess?.Version);
            Add(command, "$successObject", lastSuccess?.ObjectId);
            Add(command, "$successAt", lastSuccess?.At is { } successAt ? Stamp(successAt) : null);
            Add(command, "$detail", lastAttempt.Detail);
            command.ExecuteNonQuery();
        }
    }

    private void InitializeSchema()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS inventory_identities (
                packager_uid TEXT NOT NULL,
                application_id TEXT NOT NULL,
                profile_id TEXT NOT NULL,
                profile_revision INTEGER,
                updated_at TEXT NOT NULL,
                PRIMARY KEY(packager_uid, application_id, profile_id)
            );
            CREATE TABLE IF NOT EXISTS inventory_operations (
                operation_id TEXT PRIMARY KEY,
                kind TEXT NOT NULL,
                packager_uid TEXT NOT NULL,
                application_id TEXT,
                profile_id TEXT,
                target TEXT,
                started_at TEXT NOT NULL,
                finished_at TEXT,
                outcome TEXT NOT NULL,
                detail TEXT
            );
            CREATE TABLE IF NOT EXISTS inventory_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                event_id TEXT NOT NULL UNIQUE,
                kind TEXT NOT NULL,
                packager_uid TEXT NOT NULL,
                app_name TEXT,
                application_id TEXT,
                profile_id TEXT,
                destination TEXT,
                environment_key TEXT,
                action TEXT,
                version TEXT,
                source TEXT,
                outcome TEXT NOT NULL,
                object_id TEXT,
                build_id TEXT,
                build_record_path TEXT,
                manifest_path TEXT,
                occurred_at TEXT,
                operation_id TEXT,
                detail TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_inventory_events_app_time ON inventory_events(packager_uid, occurred_at);
            CREATE INDEX IF NOT EXISTS idx_inventory_events_operation ON inventory_events(operation_id);
            CREATE TABLE IF NOT EXISTS vendor_inventory (
                packager_uid TEXT PRIMARY KEY,
                app_name TEXT NOT NULL,
                latest_version TEXT,
                latest_source TEXT,
                last_succeeded_at TEXT,
                last_attempt_at TEXT,
                last_attempt_outcome TEXT NOT NULL,
                last_attempt_source TEXT NOT NULL,
                last_attempt_detail TEXT
            );
            CREATE TABLE IF NOT EXISTS environment_inventory (
                packager_uid TEXT NOT NULL,
                destination TEXT NOT NULL,
                environment_key TEXT NOT NULL,
                application_id TEXT,
                profile_id TEXT,
                latest_observed_version TEXT,
                latest_observation_source TEXT,
                latest_observed_at TEXT,
                last_attempt_at TEXT,
                last_attempt_outcome TEXT NOT NULL,
                last_attempt_source TEXT NOT NULL,
                last_attempt_detail TEXT,
                PRIMARY KEY(packager_uid, destination, environment_key)
            );
            CREATE TABLE IF NOT EXISTS package_inventory (
                application_id TEXT NOT NULL,
                profile_id TEXT NOT NULL,
                packager_uid TEXT NOT NULL,
                profile_revision INTEGER,
                version TEXT NOT NULL,
                build_id TEXT NOT NULL,
                build_record_path TEXT NOT NULL,
                manifest_path TEXT NOT NULL,
                staged_at TEXT NOT NULL,
                PRIMARY KEY(application_id, profile_id)
            );
            CREATE TABLE IF NOT EXISTS destination_inventory (
                packager_uid TEXT NOT NULL,
                destination TEXT NOT NULL,
                environment_key TEXT NOT NULL,
                action TEXT NOT NULL,
                application_id TEXT,
                profile_id TEXT,
                last_attempt_version TEXT,
                last_attempt_outcome TEXT NOT NULL,
                last_attempt_object_id TEXT,
                last_attempt_at TEXT,
                last_success_version TEXT,
                last_success_object_id TEXT,
                last_success_at TEXT,
                last_success_build_id TEXT,
                last_attempt_detail TEXT,
                PRIMARY KEY(packager_uid, destination, environment_key, action)
            );
            CREATE TABLE IF NOT EXISTS inventory_migrations (
                migration_id TEXT PRIMARY KEY,
                completed_at TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    private SQLiteConnection Open()
    {
        var connection = new SQLiteConnection($"Data Source={_dbPath};Version=3;Pooling=False;");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout = 30000;";
        command.ExecuteNonQuery();
        return connection;
    }

    private T Mutate<T>(Func<SQLiteConnection, SQLiteTransaction, T> mutation)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var result = mutation(connection, transaction);
        transaction.Commit();
        return result;
    }

    private static void RegisterIdentity(SQLiteConnection connection, SQLiteTransaction transaction,
        InventoryApplicationIdentity identity, DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(identity.ProfileId)) return;
        using var command = Command(connection, transaction, """
            INSERT INTO inventory_identities(packager_uid, application_id, profile_id, profile_revision, updated_at)
            VALUES($uid, $appId, $profileId, $revision, $at)
            ON CONFLICT(packager_uid, application_id, profile_id) DO UPDATE SET
                profile_revision = COALESCE(excluded.profile_revision, inventory_identities.profile_revision),
                updated_at = excluded.updated_at
            """);
        Add(command, "$uid", identity.PackagerUid);
        Add(command, "$appId", identity.ApplicationId);
        Add(command, "$profileId", identity.ProfileId);
        Add(command, "$revision", identity.ProfileRevision);
        Add(command, "$at", Stamp(at));
        command.ExecuteNonQuery();
    }

    private static void InsertEvent(SQLiteConnection connection, SQLiteTransaction transaction, InventoryEvent item)
    {
        using var command = Command(connection, transaction, """
            INSERT INTO inventory_events(event_id, kind, packager_uid, app_name, application_id, profile_id, destination,
                environment_key, action, version, source, outcome, object_id, build_id, build_record_path, manifest_path,
                occurred_at, operation_id, detail)
            VALUES($eventId, $kind, $uid, $appName, $appId, $profileId, $destination, $environment, $action, $version,
                $source, $outcome, $objectId, $buildId, $buildRecordPath, $manifestPath, $at, $operationId, $detail)
            """);
        Add(command, "$eventId", Guid.NewGuid().ToString("N"));
        Add(command, "$kind", item.Kind);
        Add(command, "$uid", item.PackagerUid);
        Add(command, "$appName", item.AppName);
        Add(command, "$appId", item.ApplicationId);
        Add(command, "$profileId", item.ProfileId);
        Add(command, "$destination", item.Destination);
        Add(command, "$environment", item.EnvironmentKey);
        Add(command, "$action", item.Action);
        Add(command, "$version", item.Version);
        Add(command, "$source", item.Source);
        Add(command, "$outcome", item.Outcome.ToString());
        Add(command, "$objectId", item.ObjectId);
        Add(command, "$buildId", item.BuildId);
        Add(command, "$buildRecordPath", item.BuildRecordPath);
        Add(command, "$manifestPath", item.ManifestPath);
        Add(command, "$at", item.At is { } at ? Stamp(at) : null);
        Add(command, "$operationId", item.OperationId);
        Add(command, "$detail", item.Detail);
        command.ExecuteNonQuery();
    }

    private static SQLiteCommand Command(SQLiteConnection connection, SQLiteTransaction transaction, string text)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = text;
        return command;
    }

    private static IReadOnlyList<InventoryEventRecord> ReadEvents(SQLiteCommand command)
    {
        using var reader = command.ExecuteReader();
        var events = new List<InventoryEventRecord>();
        while (reader.Read())
            events.Add(new InventoryEventRecord(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                Text(reader, 4), Text(reader, 5), Text(reader, 6), Text(reader, 7), Text(reader, 8), Text(reader, 9),
                Text(reader, 10), Text(reader, 11), ParseOutcome(reader.GetString(12)), Text(reader, 13), Text(reader, 14),
                Text(reader, 15), Text(reader, 16), MaybeTime(reader, 17), Text(reader, 18), Text(reader, 19)));
        return events;
    }

    private static InventoryOperationState ReadOperation(string operationId, SQLiteDataReader reader, int offset = 0) =>
        new(operationId, reader.GetString(offset), reader.GetString(offset + 1), Text(reader, offset + 2), Text(reader, offset + 3),
            Text(reader, offset + 4), ParseTime(reader.GetString(offset + 5)), MaybeTime(reader, offset + 6),
            ParseOutcome(reader.GetString(offset + 7)), Text(reader, offset + 8));

    private static void Add(SQLiteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static string? Text(SQLiteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DateTimeOffset? MaybeTime(SQLiteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : ParseTime(reader.GetString(ordinal));

    private static string Stamp(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTime(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static InventoryOutcome ParseOutcome(string value) =>
        Enum.TryParse<InventoryOutcome>(value, ignoreCase: false, out var outcome) ? outcome : InventoryOutcome.Unknown;

    private static void ValidateIdentity(InventoryApplicationIdentity identity, bool requireProfile = false)
    {
        ArgumentNullException.ThrowIfNull(identity);
        Required(identity.PackagerUid, nameof(identity.PackagerUid));
        Required(identity.ApplicationId, nameof(identity.ApplicationId));
        if (requireProfile) Required(identity.ProfileId ?? "", nameof(identity.ProfileId));
        if (!ApplicationIds.IsValid(identity.ApplicationId))
            throw new ArgumentException($"Invalid Workbench application ID '{identity.ApplicationId}'.", nameof(identity));
        if (!string.IsNullOrWhiteSpace(identity.ProfileId)) ApplicationIds.AssertProfileId(identity.ProfileId);
    }

    private static void ValidateEnvironmentObservation(string uid, InventoryEnvironmentIdentity environment,
        InventoryApplicationIdentity? identity)
    {
        Required(uid, nameof(uid));
        ValidateEnvironment(environment);
        if (identity is not null)
        {
            ValidateIdentity(identity);
            if (!string.Equals(uid, identity.PackagerUid, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The packager UID and Workbench identity must describe the same catalog item.", nameof(identity));
        }
    }

    private static void ValidateEnvironment(InventoryEnvironmentIdentity environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        Required(environment.Destination, nameof(environment.Destination));
        Required(environment.Key, nameof(environment.Key));
    }

    private static void Required(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("A non-empty value is required.", name);
    }

    private sealed record InventoryEvent(string Kind, string PackagerUid, string? AppName, string? ApplicationId,
        string? ProfileId, string? Destination, string? EnvironmentKey, string? Version, string? Source,
        InventoryOutcome Outcome, string? ObjectId, string? BuildId, string? BuildRecordPath,
        DateTimeOffset? At, string? OperationId, string? Detail, string? Action = null, string? ManifestPath = null);

    private sealed class StringTupleComparer : IEqualityComparer<(string Destination, string Action)>
    {
        public static readonly StringTupleComparer Instance = new();
        public bool Equals((string Destination, string Action) x, (string Destination, string Action) y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.Destination, y.Destination) && StringComparer.OrdinalIgnoreCase.Equals(x.Action, y.Action);
        public int GetHashCode((string Destination, string Action) obj) => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Destination), StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Action));
    }
}
