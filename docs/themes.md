# Themes

A theme is a set of centralized design tokens. Installer controls (added in a later phase) will reference tokens only; nothing is hard-coded per control.

## Tokens
- **Colours** (all required): `background`, `surface`, `surfaceAlt`, `border`, `textPrimary`, `textSecondary`, `accent`, `accentText`, `success`, `warning`, `error`. Format `#RRGGBB` or `#AARRGGBB`.
- **Typography**: font family and four sizes (caption, body, subtitle, title; 8-96).
- **Shape**: corner radius (0-32), control height (20-64), spacing (0-48).
- **Material**: Solid, Mica or Acrylic. The preview in Theme Studio does not render Mica/Acrylic; the value is stored for the installer runtime.
- **Background image**: optional id of a project asset.

## Validation
Errors: bad id/name, missing or malformed colours, out-of-range sizes, text/background contrast below 3:1 (`textPrimary` on `background` and `surface`, `accentText` on `accent`).
Warnings: contrast below 4.5:1, unknown colour tokens, non-increasing font sizes. Codes PAIC2001-2032.

## Where themes live
- **In the project** (`.paproj` `theme` section). Only these can be edited in Theme Studio. Exactly one is active.
- **User library** (`%LocalAppData%\PAIC\themes\*.paictheme`), reusable across projects. Saving to the library clears the background image reference because asset ids are project-specific.
- **Built-in**: Fluent Light, Fluent Dark, Midnight Blue (read-only; "Add to project" makes an editable copy).

## Files
`.paictheme` is JSON: `{ "format": "paic-theme", "schemaVersion": 1, "theme": { ... } }`. Import validates the whole theme and refuses invalid files.
Importing a theme whose id equals a built-in id renames it (`-imported`).

## Independence from the application theme
The app's own light/dark setting (Settings page) is separate from the installer theme stored in the project.
