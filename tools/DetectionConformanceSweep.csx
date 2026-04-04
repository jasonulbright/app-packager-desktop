// Detection Conformance Sweep
// Validates all definitions against the InstallerIdentificationService pipeline.
// Run from repo root: dotnet script tools/DetectionConformanceSweep.csx
//
// For now, this is a design document. The actual implementation runs inside the engine
// as part of StagingService — when InstallerIdentificationService is available and the
// definition's detection references {ProductCode} without a ProductCode available,
// the engine automatically falls back to metadata-derived detection.
//
// This file documents the sweep logic for reference.

/*
SWEEP ALGORITHM:

foreach definition in Definitions/:
    1. Check InstallerFramework field is populated
    2. If InstallerType == "MSI":
       - Detection should use RegistryKeyValue with {ProductCode} template
       - ProductCode resolved at staging from MSI COM inspection
       - NO CHANGE NEEDED — this path is fully automated

    3. If InstallerFramework == "WixBurn":
       - 7z extract → inner MSI → COM inspection → ProductCode
       - Detection should use RegistryKeyValue with {ProductCode}
       - Engine now resolves this automatically via InstallerIdentificationService
       - REMOVE hardcoded detection paths where applicable

    4. If InstallerFramework == "NSIS":
       - 7z extract → product-info.json OR primary EXE version
       - Detection: RegistryKey existence (JetBrains) or File detection (others)
       - Engine derives from extracted metadata
       - VERIFY against current hardcoded detection

    5. If InstallerFramework == "InnoSetup":
       - 7z extract → {app}/ directory → primary EXE → RegistryKey with _is1 pattern
       - Engine derives from extracted metadata
       - VERIFY against current hardcoded detection

    6. If InstallerFramework == "MSIX":
       - AppxManifest.xml → Identity Name + Version
       - Detection: native MSIX (CM handles)
       - No hardcoded detection needed

    7. If InstallerFramework == "ZIP":
       - Extract → primary EXE → File detection with version
       - VERIFY install path matches definition

    8. If InstallerFramework == "ODT" or "NativeEXE":
       - Cannot auto-derive detection (no standard manifest)
       - Keep manual detection config
       - FLAG for review if detection seems wrong

OUTPUT:
    - conformance-report.json with per-app results
    - Summary: N auto-derivable, N manual-required, N mismatches
*/
