# Changelog

## 0.4.0 - Phase 4 (Installer Engine and Runtime)
- `PAIC.InstallerEngine`: transactional install engine (install, upgrade, repair, modify, uninstall), append-only transaction journal with
  rollback and crash recovery, install state, file integrity verification, nine operation handlers (copy files, delete files, create folder,
  shortcut, registry, environment variable, run command, Windows service, permissions), prerequisite manager (detection, version ranges,
  dependency order, https download with mandatory SHA-256 check, silent install), virtual machine for tests and sandbox runs, command-line parser.
- `PAIC.InstallerRuntime`: Windows implementations (registry, environment, shortcuts, processes, services via sc.exe, ACLs, HTTPS download,
  detection, locks) and `InstallerHost`, which runs the install / uninstall / repair / modify / version / status / plan / prerequisites commands.
- `PAIC.InstallerCli`: `paic-installer.exe`, a single-file command-line installer used for sandbox and dry-run testing and as the uninstaller.
- Operation parameters are now defined by a shared catalog (`OperationCatalog`) and validated (PAIC1310-1315); prerequisites gained detection,
  architecture, dependencies and checksum rules (PAIC1309, PAIC1317-1319). Built-in templates use the final parameter names and include copy operations.
- Project model: operations can have a condition; prerequisites carry detection settings.
- App: Installation page (components, operations, prerequisites editors, plan, virtual test install).
- New test project `PAIC.Tests.Windows` (real Windows implementations and a sandboxed end-to-end command-line run).

## 0.3.0 - Phase 3 (Installer Designer)
- `PAIC.Controls`: element model, property contracts for 17 built-in controls, layout engine (absolute, vertical and horizontal stacks),
  conditions, `{Variable}` text templating, element validation, custom controls (definition, expansion, validation, `.paiccontrol`
  files, per-user library).
- `PAIC.Designer`: `DesignerDocument` (selection, add, delete, cut/copy/paste, duplicate, group/ungroup, align, distribute, spacing,
  match size, z-order, drag/resize interactions with drop-to-container, snapping, undo/redo), `PageService` (add, duplicate, remove,
  reorder, reset layout, reference tracking) and `PreviewSession` (navigation, conditions, variables).
- `.paproj` pages now carry `elements`, `background`, `allowBack`, `allowCancel`, `nextPageId`, `backPageId`, `visibleWhen`; the project
  carries `customControls` and the installer window size. Files from 0.2.0 still open (schema stays at 1, changes are additive).
- Default layouts for all 12 page types; new projects and built-in templates now start with real page content.
- Validation: PAIC1601-1610 (elements), PAIC1620-1626 (pages and navigation), PAIC1701-1709 (custom controls).
- App: Designer page (canvas, toolbox with drag and drop, inspector, layers, page list, custom controls, zoom, snapping, interactive preview
  with theme / window size / display scale switching and editable variables).

## 0.2.0 - Phase 2 (Assets, Themes, Templates)
- `PAIC.AssetManager`: import (with categorisation, SHA-256 and duplicate detection), folders, rename, move, copy, delete with
  reference checks, sync with disk, duplicate finder. All paths are confined to the project's `assets/` folder.
- `PAIC.ThemeEngine`: design-token themes (colours, typography, shape, material, background image), validation including
  WCAG contrast checks, `.paictheme` import/export, user theme library, three built-in themes.
- `PAIC.TemplateEngine`: ten built-in templates that are real projects, `.paictemplate` import/export (with bundled assets),
  save-open-project-as-template, create-project-from-template.
- `.paproj` gains a `theme` section (projects embed the themes they use). Files from 0.1.0 still open (schema stays at 1,
  the change is additive); validation warns when a project has no theme.
- Project validation: theme rules (PAIC1501-1503, PAIC20xx), unknown operation kinds (PAIC1308), no "no pages" warning for ZIP-only projects.
- `IProjectService.CreateAsync` can now start from a prepared project and bundled files.
- App: Assets, Themes (Theme Studio with live preview) and Templates pages; Home gets "Browse templates".

## 0.1.0 - Phase 1 (Foundation)
- Solution structure, shared build properties, GitHub Actions build workflow.
- `PAIC.Core`: validation framework, path-traversal-safe path helpers, SHA-256 hashing.
- `PAIC.Diagnostics`: structured logging (JSON-lines file log + in-memory sink).
- `PAIC.ProjectSystem`: versioned `.paproj` schema with migration support, project service
  (new / open / save / save as / close), atomic saves, recent projects, project validation rules.
- `PAIC.App`: WinUI 3 shell with Home, Project, Diagnostics and Settings pages.
- Unit tests for the libraries above.
