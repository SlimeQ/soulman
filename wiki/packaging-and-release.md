# Packaging and Release Notes

## ClickOnce publish target

`Publish-Installer.ps1` publishes `src/Soulman/Soulman.csproj` with:

- `PublishProfile=src/Soulman/Properties/PublishProfiles/WinClickOnce.pubxml`
- `TargetFramework=net8.0-windows`

The explicit `TargetFramework` is required because `Soulman.csproj` is multi-targeted (`net8.0-windows;net8.0` on Windows). Without it, MSBuild publish fails with `NETSDK1129`.
