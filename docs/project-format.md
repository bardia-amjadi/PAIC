# Project format (.paproj, schema version 1)

A `.paproj` file is indented JSON (camelCase, enums as strings) stored in the project directory:

```
MyProject/
  MyProject.paproj
  assets/            all project assets live here
```

Top-level sections: `schemaVersion`, `metadata`, `application`, `architecture`, `installation`, `pages`, `assets`,
`components`, `prerequisites`, `operations`, `build`, `security`.

## Rules
- Every path in the file is relative to the project directory, uses `/`, and asset paths must be under `assets/`.
  Absolute paths, drive letters, `..` segments and alternate streams are rejected by validation.
- `security` stores only a certificate thumbprint reference and a timestamp URL. Private keys are never stored.
- Saving writes `<file>.tmp` and then moves it over the target, so an interrupted save does not corrupt the project.
- Save As copies the `assets/` folder to the new location (reparse points are skipped), so a project can be moved between computers.

## Versioning
`schemaVersion` is checked on load. Older documents are upgraded by `ISchemaMigration` steps registered in `ProjectMigrator`.
Documents from a newer schema are refused with an explanatory error. Version 1 is the first version, so no migrations exist yet.

## Validation codes
PAIC1001-1007 metadata, PAIC1101-1102 ids, PAIC1201-1205 assets, PAIC1301-1308 references, prerequisites and operations,
PAIC1501-1503 themes (plus PAIC20xx theme rules),
PAIC1401-1402 signing settings. Missing or modified asset files are reported as errors.

## Theme section (added in 0.2.0)
`theme` holds `activeThemeId` and `themes`, a list of full theme definitions, so a project carries the themes it uses.
The section is optional when reading; files written by 0.1.0 open with an empty theme list and validation warns (PAIC1501).
Because the change is additive the schema version stays at 1. Theme colour/typography/shape rules are described in `themes.md`.
A theme's `backgroundImageAssetId` must name an entry in `assets` (PAIC1503).

## Pages and elements (added in 0.3.0)
Each entry in `pages` has `id`, `title`, `type`, `elements`, `background` (`default`, `token`, `color` or `image`), `allowBack`, `allowCancel`,
`nextPageId`, `backPageId` (empty = automatic) and an optional `visibleWhen` condition.
An element has `id`, `type`, `name`, `x`, `y`, `width`, `height`, `visible`, `locked`, string `properties`, optional `action`, optional `visibleWhen` /
`enabledWhen` conditions and `children` (for containers). Children use coordinates relative to their parent's content area.
`customControls` embeds the custom control definitions used by the project, and `installation.windowWidth/windowHeight` set the designer canvas.
Older files without these members load with empty pages, default background and a 720x480 window. Validation codes: PAIC1601-1610 elements,
PAIC1620-1626 pages/navigation, PAIC1701-1709 custom controls.

## Operations and prerequisites (finalised in 0.4.0)
`operations` entries have `id`, `kind`, `componentId`, an optional `condition` (same shape as page conditions) and string `parameters`.
The kinds and their parameters are defined in `OperationCatalog` and documented in `installer-engine.md`. Unknown kinds are kept (warning PAIC1308) so plug-ins can add their own.
`prerequisites` entries gained `detection`, `detectionTarget`, `detectionValue`, `architecture`, `installArguments`, `dependsOn` and `optional`.
A prerequisite with a `downloadUrl` must have a `sha256`. Files from 0.3.0 still open; prerequisites without detection are reported by validation (PAIC1319).
