using ApplicationPackagerEngine.Models.Workbench;
using ApplicationPackagerEngine.Services.Supersedence;
using ApplicationPackagerEngine.Services.Workbench;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerBuild;

/// <summary>
/// <c>--record-installed-value</c> and <c>--accept-exact-block</c>: writes one value of the supersedence settings for one title and one
/// version, then ends. The title resolves like in a build; nothing is staged, packaged or sent to a site.
/// </summary>
public sealed class InstalledValueRunner(SettingsService settingsService, WorkbenchApplicationCatalog applicationCatalog, IBuildOutput output)
{
    public int Run(BuildOptions options)
    {
        if (!options.IsSupersedenceRecord) throw new ArgumentException("The options carry no supersedence record.", nameof(options));
        try
        {
            var store = WorkbenchStore.Open();
            var discovered = store.DiscoverApplications(applicationCatalog.Roots());
            var applicationId = ApplicationResolver.Resolve(options.Application, discovered);
            if (!ApplicationIds.IsByo(applicationId) && ApplicationResolver.Source(applicationId, discovered) is null)
                throw new InvalidOperationException($"The application '{options.Application}' is not in the catalog, so nothing is recorded for it.");
            var version = options.Version ?? throw new InvalidOperationException("--version is required.");
            var settings = settingsService.Load();
            if (settingsService.IsFileUnreadable || settingsService.TakeLoadWarning() is not null)
                throw new InvalidOperationException("The settings file cannot be read, so nothing is written. Repair it or close the program that holds it, then run again.");
            settings.Supersedence ??= new SupersedenceSettings();

            string key;
            if (options.RecordInstalledValue is { } value)
            {
                key = InstalledValueRecorder.Record(settings.Supersedence, applicationId, version, value);
                output.Info($"Recorded installed value '{value}' for {applicationId} {version} ({key}).");
            }
            else
            {
                key = InstalledValueRecorder.AcceptBlock(settings.Supersedence, applicationId, version);
                output.Info($"Accepted the block of the Exact guard for {applicationId} {version} ({key}); the clause is written as minimum.");
            }
            settingsService.Save(settings);
            return 0;
        }
        catch (InvalidOperationException ex)
        {
            output.Error(ex.Message);
            return 1;
        }
    }
}
