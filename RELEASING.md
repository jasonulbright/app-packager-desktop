# Releasing AppPackager Desktop

Maintainer procedure for publishing a release. Run every command from the repository root with `gh` signed in. The GitHub Actions workflow `release.yml` builds, signs, tags and publishes. Nothing is built or signed on a workstation.

## 1. Preconditions

- You are on `main`, the working tree is clean and `main` is level with `origin/main`.
- Every commit on `main` is part of a release.
- The engine tests pass:

```bash
dotnet test tests/ApplicationPackagerEngine.Tests/ApplicationPackagerEngine.Tests.csproj --configuration Release --nologo
```

Any failure stops the release. The workflow runs the same tests again.

## 2. Version

The version is `YYYY.MDD.N`.

- Field 1 is the year.
- Field 2 is the month and day as one integer without zero padding (October 3 is `1003`, October 17 is `1017`).
- Field 3 is the running release number. The first release is `1`. Each later release adds `1` to the newest release.
- No field has a leading zero. Every field is below 65536.

Set `<Version>` in `src/ApplicationPackagerUI/ApplicationPackagerUI.csproj`. Check the version against the existing tags:

```powershell
& .\tools\test-release-version.ps1 -Version <ver> -ExistingTag @(git tag -l)
```

The script throws with the broken rule and prints nothing when the version is valid.

## 3. Changelog

Rename `## Unreleased` in `CHANGELOG.md` to `## [<ver>] - <yyyy-MM-dd>`.

- Group the bullets under `###` sections by kind of change.
- A bullet starts with a verb, states one change and has at most 15 plain words. A bullet has no file name, ticket reference or rationale.
- The section must not be empty. The workflow stops when the section is missing or empty.
- The section of the first release holds the single line `Initial release`. The workflow then publishes the notes `Initial release` and ignores the headline.

## 4. Commit and push

```bash
git add src/ApplicationPackagerUI/ApplicationPackagerUI.csproj CHANGELOG.md
git commit -m "Release <ver>"
git push origin main
```

## 5. Run the workflow

```bash
gh workflow run release.yml -R jasonulbright/app-packager-desktop --ref main -f headline="<one outcome metric>"
gh run watch -R jasonulbright/app-packager-desktop $(gh run list -R jasonulbright/app-packager-desktop --workflow release.yml -L 1 --json databaseId --jq '.[0].databaseId')
```

The headline becomes the `##` line of the release notes. It is one concrete outcome metric, for example `## 320 definitions`. It is not required for the `Initial release` section.

The workflow:

1. Refuses to run off `main`.
2. Reads the version from the UI project, checks it with `tools/test-release-version.ps1` and checks that the tag and the release do not exist.
3. Reads the `## [<ver>]` section of `CHANGELOG.md` and writes the release notes: `## <headline>`, the section, and `Full changelog: CHANGELOG.md`.
4. Runs the engine tests.
5. Publishes the application and `AppPackagerBuild.exe` with `tools/build-release.ps1 -PublishOnly`.
6. Signs the published executables, builds the installer from the signed stage with `tools/build-installer.ps1`, and signs the installer.
7. Verifies every signature with `tools/verify-signatures.ps1`.
8. Zips the stage, writes `checksums.txt`, tags the commit `v<ver>` and publishes the release with the installer, the zip and `checksums.txt`.

The signing identity comes from the federated credential of the Azure app registration. The credential must trust the subject of the run. The run takes no environment, so the subject has the form `repo:<owner>@<owner id>/<repository>@<repository id>:ref:refs/heads/main`.

## 6. Verify the published release

```bash
mkdir -p verify && cd verify
gh release download v<ver> -R jasonulbright/app-packager-desktop --clobber
sha256sum -c checksums.txt
powershell -NoProfile -Command "Get-AuthenticodeSignature .\AppPackagerDesktop-Setup-<ver>.exe | Format-List Status, SignerCertificate, TimeStamperCertificate"
git ls-remote --tags origin v<ver>
```

- Both files in `checksums.txt` must report `OK`.
- The signature status must be `Valid` and the signature must carry a timestamp.
- The release must not be a draft and must list three assets: the installer, the zip and `checksums.txt`.
- The remote tag must exist.

## 7. If a release is wrong

Delete the release and the tag, fix the cause, and run the procedure again with the same version:

```bash
gh release delete v<ver> -R jasonulbright/app-packager-desktop --yes --cleanup-tag
```
