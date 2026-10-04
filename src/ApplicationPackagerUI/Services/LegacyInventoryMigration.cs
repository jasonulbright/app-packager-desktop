using System.Globalization;
using ApplicationPackagerEngine.Models.Inventory;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.OneClick;
using ApplicationPackagerUI.Models;

namespace ApplicationPackagerUI.Services;

/// <summary>Moves the operational AppStates snapshot from settings.json into the shared inventory.</summary>
public sealed class LegacyInventoryMigration(SettingsService settingsService, ApplicationInventoryService inventory)
{
    public bool Run()
    {
        var settings = settingsService.Load();
        if (settingsService.IsFileUnreadable || settings.AppStates.Count == 0) return false;

        var legacyStates = settings.AppStates
            .Where(pair => pair.Value is not null)
            .Select(pair => ToInventoryState(pair.Key, pair.Value))
            .ToArray();

        // The DB transaction commits before settings are cleared. If the process stops
        // between them, the durable migration marker makes the next run a safe replay.
        var imported = inventory.ImportLegacyAppStates(legacyStates, DateTimeOffset.UtcNow);
        settings.AppStates.Clear();
        settingsService.Save(settings);
        return imported;
    }

    private static LegacyInventoryAppState ToInventoryState(string uid, AppCheckState state)
    {
        var facts = new List<LegacyInventoryDestinationFact>();
        foreach (var (destination, record) in state.Published ?? [])
        {
            if (record is null) continue;
            facts.Add(new LegacyInventoryDestinationFact(destination, record.Version ?? "", record.Id,
                ParseAt(record.At), InventoryOutcome.Succeeded));
        }
        foreach (var (destination, record) in state.NotSupported ?? [])
        {
            if (record is null) continue;
            facts.Add(new LegacyInventoryDestinationFact(destination, record.Version ?? "", null,
                ParseAt(record.At), InventoryOutcome.Refused, record.Reason));
        }

        return new LegacyInventoryAppState(uid, state.LatestVersion ?? "", Offset(state.LastChecked),
            state.CurrentVersion ?? "", Offset(state.LastStaged), facts);
    }

    private static DateTimeOffset? Offset(DateTime? value) => value is null ? null : new DateTimeOffset(value.Value).ToUniversalTime();

    private static DateTimeOffset? ParseAt(string? value) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed) ? parsed : null;
}
