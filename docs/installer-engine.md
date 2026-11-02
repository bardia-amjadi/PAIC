# Installer engine

The engine (`PAIC.InstallerEngine`) installs, upgrades, repairs, modifies and uninstalls a product from a project and its payload. It is independent of the PAIC application and of the wizard UI.
Everything machine-specific (registry, services, shortcuts, processes, downloads, folders) sits behind interfaces; `PAIC.InstallerRuntime` provides the Windows implementations and `VirtualSystem` provides a simulated machine for tests, dry runs and sandbox installs.

## Transactions
Every install, upgrade, repair and modify is one transaction. Before an operation changes anything its undo information is appended to `.paic\journal.jsonl` (flushed to disk).
- **Failure or cancellation** undoes the journal in reverse: files are deleted or restored from backup, registry values and environment variables go back to their previous values, services and permission rules added are removed, created folders are removed when empty.
- **Crash or power loss**: the next run (install, uninstall or status) finds the open journal and rolls it back; a journal that had already committed is finalised instead.
- **Commit** writes the new `state.json` (also stored in the commit line of the journal), then removes the journal and backups.
- Not everything can be undone. Programs started by *Run command* can only be reversed through their optional uninstall command (a compensating action); without one the engine logs that the effects could not be undone. Prerequisites installed before the transaction starts stay installed.

## Operations
Parameters accept `{InstallDir}`, `{ProductName}`, `{Version}`, `{Publisher}`, `{ProductId}`, wizard variables (`{Name}`) and `%ENVIRONMENT%` folders. Every operation can have a component and a condition.

| Kind | Parameters (* required) | Notes |
|---|---|---|
| `copyFiles` | `source`*, `destination`*, `overwrite` (always / ifDifferent / never), `userData`, `optional`, `allowOutside` | Source is a glob over project assets (`assets/app/**`). Each file is copied next to the target, verified against its SHA-256, the existing file is backed up, then moved into place. |
| `deleteFiles` | `path`* (wildcards in the file name), `recursive` | Only inside the install folder. Originals are backed up for rollback; deletion is final after commit. |
| `createDirectory` | `path`*, `allowOutside` | Folders created are removed on uninstall when empty. |
| `createShortcut` | `location`* (startMenu / desktop / startup), `name`*, `target`*, `arguments`, `workingDirectory`, `icon`, `description`, `folder` | All-users locations for per-machine installs. |
| `writeRegistry` | `root`* (HKCU / HKLM), `key`*, `name`, `value`*, `type` (string / expandString / dword / qword / multiString) | Previous value restored on rollback; the value is removed on uninstall unless someone changed it. |
| `setEnvironmentVariable` | `name`*, `value`*, `action` (set / append / prepend), `scope` (user / machine) | Append and prepend add one entry and remove only that entry again. |
| `runCommand` | `command`*, `arguments`, `workingDirectory`, `runOn` (install / uninstall), `onRepair`, `uninstallCommand`, `uninstallArguments`, `successCodes`, `timeoutSeconds` | Started without a shell. Not reversible by itself. |
| `installService` | `name`*, `binaryPath`*, `displayName`, `arguments`, `startType`, `account`, `description`, `start` | Built-in accounts only. Refuses to take over a service it did not install. |
| `setPermissions` | `path`*, `principal`*, `rights` (read / modify / fullControl), `allowOutside` | Removes only the rule it added. |

### Safety rules
- Destinations must be inside the install folder unless the operation sets `allowOutside`; protected system folders (Windows, System32) are refused even then. `deleteFiles` never leaves the install folder.
- The engine only deletes things it recorded. Files that were not installed by the product are never touched; a folder is removed only if the installer created it **and** it is empty.
- Files marked `userData`, and files modified after installation, are kept on uninstall unless the user explicitly asks for removal (`--remove-user-data`).
- Uninstall removes registry values and variables the installer wrote; it does not restore values that existed before the first install (rollback does).
- Files in `.paic` (state, journal, backups) are never touched by operations.

## Upgrade, repair, modify
- **Upgrade**: installing a newer version over an installed one runs all operations with backups; things the old version installed that the new one no longer does are removed after the commit. Downgrades are refused unless allowed. The same version again does nothing (`AlreadyInstalled`).
- **Repair**: re-runs the selected operations; files that are missing or differ are restored, `runCommand` operations are skipped unless `onRepair`.
- **Modify**: installs the operations of newly selected components and removes everything belonging to deselected components. Required components cannot be removed.

## Prerequisites
Each prerequisite has a detection method (registry value, file version, .NET shared runtime, Windows version), a minimum and maximum version, an architecture it applies to, dependencies and optionally a download.
They are evaluated in dependency order before anything is installed. A missing or too-old prerequisite is downloaded over https into a fresh temporary folder, **verified against its SHA-256 (mandatory)**, run with its silent arguments (exit codes 0, 3010 and 1641 are accepted) and detected again. A version above the maximum cannot be fixed and fails the setup; optional prerequisites only warn.

## State, logs and history
`<install folder>\.paic\state.json` lists everything installed (with SHA-256 of each file). Add/Remove Programs gets an entry under `...\Uninstall\{ProductId}` (HKCU for per-user, HKLM otherwise).
Logs and `history.jsonl` are written outside the install folder (`%LocalAppData%\PAIC\InstallerLogs` or `%ProgramData%\PAIC\InstallerLogs`) so they survive uninstall.

## Elevation
The engine reports why administrator rights are needed (per-machine install, Program Files, HKLM, machine variables, services, permissions, all-users shortcuts) and does nothing when they are missing. The command line can relaunch itself elevated with `--elevate` (never in silent mode).

## Known limitations
- The uninstaller is the same `paic-installer.exe`, copied to `.paic\uninstall.exe`; it has no graphical interface yet (the wizard arrives with the package formats). Add/Remove Programs "Change" and "Repair" are disabled for now.
- A running uninstaller cannot delete itself; a hidden command removes it after the process exits.
- Files in use are not replaced on reboot; a locked file fails the operation and rolls the install back.
- Service accounts are limited to LocalSystem, LocalService and NetworkService.
