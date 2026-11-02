# Contributing

- The GitHub Actions workflow (`.github/workflows/build.yml`) is the official build. A change is not done until it is green.
- Keep UI code in `PAIC.App`; logic must live in libraries that do not reference WinUI.
- Add unit tests for every library change (`tests/PAIC.Tests`).
- Validate any path coming from a project file with `PathSafety` before touching the file system.
- Do not document a feature until it is implemented; update `CHANGELOG.md` and the status table in `README.md`.
