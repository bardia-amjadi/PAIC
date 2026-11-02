# Building

The official build is `.github/workflows/build.yml` on `windows-latest`. It restores, builds Debug and Release (x64),
runs the unit tests, publishes the self-contained app, verifies the output, zips it with a SHA-256 checksum and
metadata file, uploads the artifacts and writes a build report that uses the real job status.

Notes
- Package versions `Microsoft.WindowsAppSDK 1.8.*` and `Microsoft.Windows.SDK.BuildTools 10.0.26100.*` float to the newest matching release. Pin exact versions once the first build succeeds.
- The app is unpackaged and self-contained (`WindowsPackageType=None`, `WindowsAppSDKSelfContained=true`), so no MSIX tooling or installed runtime is needed to run it.
- Use `-p:Platform=x64` when building the solution or the app project from the command line.
