# Packaging and Release Notes

## ClickOnce publish target

`Publish-Installer.ps1` publishes `src/Soulman/Soulman.csproj` with:

- `PublishProfile=src/Soulman/Properties/PublishProfiles/WinClickOnce.pubxml`
- `TargetFramework=net8.0-windows`

The explicit `TargetFramework` is required because `Soulman.csproj` is multi-targeted (`net8.0-windows;net8.0` on Windows). Without it, MSBuild publish fails with `NETSDK1129`.

## Startup behavior in release builds

- `net8.0-windows` publishes as `WinExe`, so there is no visible console window for startup messages.
- Soulman enforces a single instance with a named mutex (`Global\Soulman.Instance`).
- Duplicate launches now show an "already running" dialog on Windows so startup failures are not silent.
- If the tray UI crashes during startup, Soulman now stops the host process instead of continuing headless, so relaunch attempts are not blocked by a hidden background instance.
- Tray startup crashes in interactive Windows sessions now also show a dialog that points users to `%LOCALAPPDATA%\Soulman\logs`.
