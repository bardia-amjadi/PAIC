# Asset Manager and Template Library

## Assets
Everything lives under the project's `assets/` folder and is listed in the project (`assets` section, with id, path, category, SHA-256).
- **Import** copies files (button or drag-and-drop) into `assets/<category>/` or the selected folder, renames on collisions (`name (2).ext`), computes SHA-256 and reports files with identical content. Policy `Skip` omits duplicates.
- **Folders, rename, move, copy, delete** keep the project list in sync. Moves and renames update paths; copies get new ids.
- **Delete** is refused when the asset is the application icon or a theme background, unless you confirm clearing those references.
- **Sync with disk** registers untracked files and lists (optionally removes) listed assets whose files are missing.
- **Duplicates** groups assets by SHA-256.
- Every path is validated: relative, no `..`, no drive letters or streams, always inside `assets/`. Links (reparse points) are skipped.
- Previews: images (PNG, JPG, BMP, GIF, ICO, WebP, SVG), text up to 20,000 characters, and metadata for everything else.
- Not implemented yet: references from designer elements (the designer arrives in a later phase), so only icon and theme background are checked.

## Templates
Ten built-in templates (Minimal, Fluent, Dark, Light, Developer Tool, Desktop Application, Multi-component, Runtime, Enterprise, Portable). Each is a real project definition: pages, components, prerequisites, operations, theme and output formats.
- `{ProductName}` is replaced by the new project's name when a project is created from a template (install directory, operation parameters, product name).
- `{InstallDir}` is stored as-is for the installer engine to resolve later. **Operation parameter names in templates are provisional** until the engine phase defines them.
- Templates define page types and titles only; page layouts come with the designer phase.
- **Custom templates**: "Save open project as template" captures the project and its assets into `%LocalAppData%\PAIC\templates\<id>.paictemplate`. Built-ins are immutable; to modify one, create a project from it and save a new template.
- **Import/export** uses `.paictemplate` (JSON with the project and base64 assets, up to 50 MB). Import checks the format, every embedded path, asset checksums, that every listed asset has a file and vice versa, and runs project validation. Template ids that clash with built-ins are renamed.
