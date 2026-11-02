# Command line (`paic-installer`)

```
paic-installer <command> [options]
```
| Command | What it does |
|---|---|
| `install` | Install, or upgrade an existing installation |
| `uninstall` | Remove the product (also what `.paic\uninstall.exe` runs) |
| `repair` | Restore missing or damaged files and settings |
| `modify` | Change the selected components |
| `version` | Show installer, package and installed versions |
| `status` | Show whether the product is installed and whether files are intact |
| `plan` | Show what install would do, without changing anything |
| `prerequisites` | Check prerequisites |

| Option | Meaning |
|---|---|
| `-s`, `--silent` (`/s`, `/quiet`) | No questions, no progress output |
| `-d`, `--install-dir <path>` | Install folder |
| `-c`, `--components <a,b\|all>` | Components (required ones are always included) |
| `--project <file.paproj>` | Use a project folder as the payload (development and testing; packaged installers will supply their own) |
| `--set Name=Value` | Installer variable (repeatable) |
| `--accept-license` | Required for silent installs of products with a license page |
| `--no-prerequisites` | Do not install missing prerequisites (they still must be satisfied) |
| `--remove-user-data` | On uninstall also remove user data and modified files |
| `--allow-downgrade` | Allow replacing a newer version |
| `--elevate` | Ask for administrator rights when needed (interactive only) |
| `--dry-run` | Print the plan for install / repair / modify and change nothing |
| `--sandbox <folder>` | Run against a virtual machine rooted in the folder: files are real but live in the folder; registry, environment, services and permissions are simulated and stored in `virtual-system.json` |
| `--log <folder>` | Folder for the install log |
| `--json` | Machine-readable `version` and `status` |

## Exit codes
`0` success · `1` failure · `2` invalid arguments · `3` cancelled (changes rolled back) · `4` prerequisites not satisfied · `5` not installed · `740` administrator rights required.
Ctrl+C cancels the running operation and the engine rolls back.

## Testing safely
1. `paic-installer plan --project My.paproj` shows every step and whether elevation is needed.
2. `paic-installer install --project My.paproj --sandbox C:\Temp\sandbox --silent --accept-license` really copies files, but only inside the sandbox folder.
3. `status`, `repair` and `uninstall` with the same `--sandbox` complete the cycle; delete the folder afterwards.
The real machine is only touched when `--sandbox` is omitted.
