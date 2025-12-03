# Soulman

Background .NET service that watches a Soulseek downloads folder (e.g. `Documents/Soulseek Downloads/complete/<username>`) and moves finished audio files into your music library after reading ID3 tags. It names files `Artist/Album/## - Title.ext`, adds a disk suffix when present, and skips anything that is still growing.

## Features
- Polls the source tree on a configurable cadence and waits for files to settle before moving.
- Reads tags with TagLibSharp; falls back to filenames when tags are missing.
- Sanitizes paths and handles filename collisions with ` (1)`, ` (2)`, etc.
- Uses album artist (first entry) to keep albums together; compilations land under `Various Artists`.
- Runs as a console app or Windows service (when started with `sc`/`New-Service`); ClickOnce installer for non-admin installs, matching the lifeviz repo flow.
- System tray icon with quick links to source/destination and the ability to register "clone" folders (network shares) that receive copies of moved files.

## Quickstart
```powershell
# edit configuration (defaults shown)
Copy-Item src/Soulman/appsettings.json src/Soulman/appsettings.local.json
# set SourcePath/DestinationPath, PollIntervalSeconds, SettledSeconds

# run locally
dotnet run --project src/Soulman

# override via CLI/environment instead of config file
dotnet run --project src/Soulman -- --Soulman:SourcePath "D:\Soulseek\complete" --Soulman:DestinationPath "D:\Music"
$env:SOULMAN__SourcePath="D:\Soulseek\complete"
$env:SOULMAN__DestinationPath="D:\Music"
```

Defaults (can be overridden in config, CLI, or environment):
- `SourcePath`: `%USERPROFILE%\Documents\Soulseek Downloads\complete`
- `DestinationPath`: `%USERPROFILE%\Music`
- `AdditionalSources`: `[]` (scan extra folders from config)
- `PollIntervalSeconds`: 30
- `SettledSeconds`: 20
- Extensions: `.mp3, .flac, .wav, .aac, .m4a, .ogg, .aiff, .alac, .opus, .wv, .ape`

### Tray & clone folders
- Soulman shows a tray icon (uses `soulman.ico`). Right-click for:
  - Set Source Folder… / Set Destination Folder… (persists to `%LOCALAPPDATA%\Soulman\paths.json`)
  - Add Clone Destination… (UNC/network paths allowed) to mirror the organized library onto network drives
  - Open Source Folder / Open Destination Folder
  - View/clear current clone folders
  - Exit
- Clone folders are persisted to `%LOCALAPPDATA%\Soulman\clonefolders.json` and are treated as extra destinations; every move to the primary destination is copied to each clone.

## Running as a Windows service
1. Publish or copy `Soulman.exe` somewhere stable (e.g. `C:\Apps\Soulman\Soulman.exe`).  
2. Create the service (needs admin):
   ```powershell
   sc.exe create Soulman binPath= "\"C:\Apps\Soulman\Soulman.exe\"" start= auto
   sc.exe start Soulman
   ```
   Stop/delete with `sc stop Soulman` then `sc delete Soulman`. The app respects `appsettings.*` and `SOULMAN__` env vars at service start.

## Packaging & installer (lifeviz-style)
- `.\Publish-Installer.ps1` — resolves `MSBuild.exe` and runs the ClickOnce publish profile (`src/Soulman/Properties/PublishProfiles/WinClickOnce.pubxml`). Outputs to `src/Soulman/bin/<Configuration>/net8.0-windows/publish/`.
- `.\deploy.ps1` — builds, publishes with a time-based version stamp so ClickOnce sees an update, copies `Install-ClickOnce.ps1` alongside the payload, then launches `Soulman.application` to install/update in-place.
- `Install-ClickOnce.ps1` — stages a published payload into `%LOCALAPPDATA%\soulman-clickonce`, stamps a stable deployment provider URI, and launches `Soulman.application` from there (useful when shipping a zip of the publish folder).

ClickOnce uses the full MSBuild toolchain (not `dotnet msbuild`). If Rider/CLI defaults to `dotnet msbuild`, you'll see `MSB4803`; run the scripts above or point your IDE publish config at `MSBuild.exe`.

## Project layout
- `src/Soulman/DownloadScanner.cs` — polling, settle detection, tag read, move logic.
- `src/Soulman/SoulmanSettings.cs` — user-configurable settings and defaults.
- `src/Soulman/Worker.cs` — background host loop.
- `src/Soulman/appsettings*.json` — configuration; add `appsettings.local.json` for machine overrides.
