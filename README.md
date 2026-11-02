# Pixel Advanced Installer Creator (PAIC)

A Windows installer development environment built on C#, .NET and WinUI 3.
This repository currently contains **Phases 1-4 (Foundation; Assets, Themes, Templates; Installer Designer; Installer Engine and Runtime)** of the PAIC master plan.

## Status

| Area | Status |
|---|---|
| Solution layout, shared build config | Implemented, not yet verified by a CI run |
| `.paproj` schema (versioned, with migrations) | Implemented, unit tested |
| Project service: new / open / save / save as / close, atomic saves | Implemented, unit tested |
| Recent projects | Implemented, unit tested |
| Project validation (metadata, ids, assets, prerequisites, signing settings) | Implemented, unit tested |
| Logging and Diagnostics page | Implemented (logging unit-tested only indirectly) |
| Asset Manager (import, folders, rename/move/copy/delete, reference checks, duplicates, sync) | Implemented, unit tested; UI not compiled or run |
| Theme engine (tokens, validation, contrast checks, import/export, library) | Implemented, unit tested |
| Theme Studio page with live preview | Implemented, **not compiled or run yet** |
| Template library (10 built-in, import/export, save project as template, create from template) | Implemented, unit tested; UI not compiled or run |
| Designer model: 17 control types with property contracts, layout, conditions, templating, validation | Implemented, unit tested |
| Designer operations: add/delete/clipboard/group/align/distribute/z-order/undo-redo/page management | Implemented, unit tested |
| Custom controls (create from selection, parameters, edit, library, import/export) | Implemented, unit tested; UI not compiled or run |
| Designer page UI (canvas, inspector, layers, toolbox, interactive preview) | Implemented, **not compiled or run yet** |
| Installer engine: operations, transactions, rollback, recovery, upgrade/repair/modify/uninstall, verification | Implemented, unit tested against a virtual machine |
| Prerequisite manager (detection, ordering, verified download, silent install) | Implemented, unit tested against a virtual machine |
| Windows implementations (registry, environment, shortcuts, services, ACLs, processes, download) | Implemented, integration tests written, **not yet run** |
| `paic-installer` command line (install, uninstall, repair, modify, version, status, plan) | Implemented, sandbox end-to-end test written, **not yet run** |
| Installation page (components, operations, prerequisites, plan, virtual test install) | Implemented, **not compiled or run yet** |
| WinUI shell (Home, Project, Designer, Assets, Themes, Templates, Diagnostics, Settings) | Implemented, **not compiled or run yet** |
| GitHub Actions build (x64, Debug and Release) | Written, **not yet run** |
| Package formats (EXE bootstrapper with wizard, MSI, MSIX, ZIP), installer wizard UI, logic builder, plugins, signing, releases | Not implemented (later phases) |

"Not yet verified" means no Windows build has been run against this code. Run the workflow and fix anything it reports.

## Requirements
- Windows 10 version 1809 (build 17763) or later is the configured minimum (`TargetPlatformMinVersion`). Confirm this against the Windows App SDK release notes for the version restored by NuGet.
- .NET 10 SDK. Visual Studio 2022/2026 with the ".NET desktop development" workload, or just the `dotnet` CLI.

## Build
```powershell
dotnet restore PAIC.sln -p:Platform=x64
dotnet build   PAIC.sln -c Release -p:Platform=x64
dotnet test    tests/PAIC.Tests/PAIC.Tests.csproj
dotnet publish src/PAIC.App/PAIC.App.csproj -c Release -r win-x64 --self-contained true -p:Platform=x64 -o artifacts/publish
```
Open `PAIC.sln` in Visual Studio and choose the `x64` platform to run the app.
Only x64 is configured. ARM64 is not built or supported yet.

## Layout
```
src/PAIC.Core           validation framework, safe paths, hashing
src/PAIC.Diagnostics    logging providers
src/PAIC.Controls       element model, control contracts, layout, custom controls
src/PAIC.Designer       designer document (undo/redo, editing ops), page service, preview session
src/PAIC.InstallerEngine  install engine, operations, prerequisites, virtual machine, command-line parser
src/PAIC.InstallerRuntime Windows implementations and the command-line host
src/PAIC.InstallerCli     paic-installer.exe
src/PAIC.ProjectSystem  .paproj model, serializer, migrations, project service, validation
src/PAIC.ThemeEngine    design-token themes, validation, serializer, library
src/PAIC.AssetManager   assets folder operations and reference tracking
src/PAIC.TemplateEngine built-in and user templates, .paictemplate format
src/PAIC.App            WinUI 3 application (unpackaged, self-contained)
tests/PAIC.Tests        xUnit tests
docs/                   architecture, project format, building
```
