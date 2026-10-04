# Third-party notices

The release contains the components below. The full license texts and the notice files of the components are in the `licenses` folder.

| Component | Version | License | Copyright | Files in `licenses` |
|---|---|---|---|---|
| .NET runtime | 10.0 | MIT | Copyright (c) .NET Foundation and Contributors | `dotnet-runtime-LICENSE.txt`, `dotnet-runtime-THIRD-PARTY-NOTICES.txt` |
| .NET Windows Desktop runtime (WPF) | 10.0 | MIT | Copyright (c) .NET Foundation and Contributors | `windowsdesktop-runtime-LICENSE.txt` |
| Microsoft.Extensions.Hosting and its Microsoft.Extensions dependencies | 10.0.5 | MIT | Copyright (c) Microsoft Corporation | `dotnet-runtime-LICENSE.txt`, `dotnet-runtime-THIRD-PARTY-NOTICES.txt` |
| System.Data.SQLite | 2.0.4 | Public domain | None | None |
| SQLite native library | 3.53.4 | Public domain | Copyright 2014-2026 SourceGear, LLC (package) | None |
| CommunityToolkit.Mvvm | 8.4.2 | MIT | Copyright (c) .NET Foundation and Contributors | `communitytoolkit-mvvm-License.md`, `communitytoolkit-mvvm-ThirdPartyNotices.txt` |
| WPF-UI, WPF-UI.Abstractions, WPF-UI.DependencyInjection | 4.2.0 | MIT | Copyright (c) 2021-2025 Leszek Pomianowski and WPF UI Contributors | `wpf-ui-LICENSE.md`, `wpf-ui-ThirdPartyNotices.txt` |

The installer is built with NSIS (Nullsoft Scriptable Install System), which is under the zlib/libpng license.

The application downloads these items at run time. They are not part of the release:

- The icon pack, from its own GitHub release.
- The Microsoft Win32 Content Prep Tool, from Microsoft, under its own license terms.
- The installers of the applications that you package, from their vendors, under the license terms of each vendor.
