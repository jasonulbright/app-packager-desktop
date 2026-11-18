using ApplicationPackagerEngine.Models.Inventory;
using ApplicationPackagerEngine.Services;

// Arguments: data root of the run, packager uid (the definition identifier of the row), application identifier ("catalog:<id>"),
// title of the application on the site, version, site code, provider machine.
if (args.Length != 7)
{
    Console.Error.WriteLine("usage: SeedPendingSupersedence <data root> <packager uid> <application id> <title> <version> <site code> <provider machine>");
    return 2;
}
Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, args[0]);
var uid = args[1];
var inventory = new ApplicationInventoryService();
var identity = new InventoryApplicationIdentity(uid, args[2], "default", 0);
// A few seconds ahead, so the record is the last attempt even after an Apply supersedence that ran a moment ago.
var at = DateTimeOffset.UtcNow.AddSeconds(5);
var environment = InventoryEnvironmentIdentity.ConfigMgr(args[5], args[6]);
inventory.RecordVendorCheck(new InventoryVendorCheck(uid, args[3], args[4], "capture-seed", InventoryOutcome.Succeeded, at));
inventory.RecordStagedBuild(new InventoryStagedBuild(identity, args[4], "capture-build", "build.json", "stage-manifest.json", at));
inventory.RecordDestinationOutcome(new InventoryDestinationOutcome(uid, identity, environment, "Publish", args[4],
    InventoryOutcome.PublishedSupersedenceNotApplied, args[3], at, "capture-build",
    Detail: "Published, supersedence not applied: the site gave no answer in time."));
Console.WriteLine("Seeded the pending supersedence step of " + uid);
return 0;
