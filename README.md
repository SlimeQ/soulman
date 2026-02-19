<<<<<<< HEAD
# Soulman

Background .NET service that watches a Soulseek downloads folder (e.g. `Documents/Soulseek Downloads/complete/<username>`) and moves finished audio files into your music library after reading ID3 tags. It names files `Artist/Album/## - Title.ext`, adds a disc suffix when present, and skips anything still growing.

## Features
- Polls the source tree on a configurable cadence and waits for files to settle before moving.
- Reads tags with TagLibSharp; falls back to filenames when tags are missing.
- Ignores files that live under the destination library or any configured clone folders to avoid accidental deletions.
- Uses album artist (first entry) to keep albums together; compilations land under `Various Artists`.
- Sanitizes paths and handles filename collisions with ` (1)`, ` (2)`, etc.
- **Cross-platform**: runs on Windows (tray icon + service) and Linux (systemd user service).
- Clone destinations mirror the organized library to network shares/other drives.
- Move log UI shows recent moves (last 24h) including clone copies (Windows).
- Responds to LAN discovery so the tray can show other running Soulman hosts (UDP broadcast + multicast on port 45832).
=======
# Soulman

Background .NET service that watches a Soulseek downloads folder (e.g. `Documents/Soulseek Downloads/complete/<username>`) and moves finished audio files into your music library after reading ID3 tags. It names files `Artist/Album/## - Title.ext`, adds a disc suffix when present, and skips anything still growing.

## Features
- Polls the source tree on a configurable cadence and waits for files to settle before moving.
- Reads tags with TagLibSharp; falls back to filenames when tags are missing.
- Ignores files that live under the destination library or any configured clone folders to avoid accidental deletions.
- Uses album artist (first entry) to keep albums together; compilations land under `Various Artists`.
- Sanitizes paths and handles filename collisions with ` (1)`, ` (2)`, etc.
- Runs as a console app or Windows service; tray icon for quick controls and clone destinations.
- Clone destinations mirror the organized library to network shares/other drives.
- Move log UI shows recent moves (last 24h) including clone copies.
- Responds to LAN discovery so the tray can show other running Soulman hosts (UDP broadcast on port 45832; needs inbound allow on Private/Domain networks).
- Discovers other Soulman peers on your LAN and pulls missing library files over TCP (default 45833); skips files that already exist locally.
>>>>>>> upstream-sync
- Enforces a single running instance; duplicate launches exit immediately.

## Quickstart

### Windows
```powershell
# edit configuration (defaults shown)
Copy-Item src/Soulman/appsettings.json src/Soulman/appsettings.local.json
# set SourcePath/DestinationPath, PollIntervalSeconds, SettledSeconds

# run locally
dotnet run --project src/Soulman -f net8.0-windows

# override via CLI/environment instead of config file
dotnet run --project src/Soulman -- --Soulman:SourcePath "D:\Soulseek\complete" --Soulman:DestinationPath "D:\Music"
```

### Linux
```bash
# Build
dotnet publish src/Soulman -c Release -f net8.0 -r linux-x64 --self-contained -o publish

# One-liner install (systemd user service):
cd publish && ./install-linux.sh

# Configure (optional — defaults to ~/Downloads/SoulmanIngress -> ~/Music)
mkdir -p ~/.config/soulman
cat > ~/.config/soulman/appsettings.json << 'EOF'
{
  "Soulman": {
    "SourcePath": "/home/you/Downloads/SoulmanIngress",
    "DestinationPath": "/home/you/Music",
    "AdditionalSources": [],
    "PollIntervalSeconds": 30,
    "SettledSeconds": 20
  }
}
EOF
systemctl --user restart soulman
```

### Defaults
| Setting | Windows | Linux |
|---|---|---|
| `SourcePath` | `%USERPROFILE%\Documents\Soulseek Downloads\complete` | `~/Downloads/SoulmanIngress` |
| `DestinationPath` | `%USERPROFILE%\Music` | `~/Music` |
| `AdditionalSources` | `[]` | `[]` |
| `PollIntervalSeconds` | 30 | 30 |
| `SettledSeconds` | 20 | 20 |
| Extensions | `.mp3, .flac, .wav, .aac, .m4a, .ogg, .aiff, .alac, .opus, .wv, .ape` | same |

Override via config, CLI args, or `SOULMAN__` environment variables.

## LAN Discovery

Soulman instances discover each other via UDP on port 45832 using broadcast, directed broadcast, and multicast (`239.255.64.64`). The tray (Windows) or logs (Linux) show discovered peers.

On Windows, allow inbound UDP 45832:
```powershell
New-NetFirewallRule -DisplayName "Soulman LAN Discovery (UDP 45832)" -Direction Inbound -Action Allow -Protocol UDP -LocalPort 45832 -Profile Private,Domain
```

On Linux, if using `ufw`:
```bash
sudo ufw allow 45832/udp
```

## Install script (Windows)
- `.\install.ps1` runs the ClickOnce publish, bundles a one-file `soulman_installer.exe`, and launches it.
  ```powershell
  .\install.ps1
  ```
<<<<<<< HEAD
- Optional flags: `-Configuration`, `-PublishProfile`, `-ApplicationRevision`, `-CleanOutput`.

## Scan flow & safety
- Enumerates configured source folders (skips anything under the destination or clone trees) and only looks at supported extensions.
- Tracks observed size/timestamp and only moves a file after it has been stable for `SettledSeconds`.
- Builds target paths from tags; `(Disc #)` is only added for multi-disc albums.
- Moves the organized file into the destination, clones it to any configured clone roots, and logs the result.
- Emits warnings when a protected path is encountered.

## Running as a service

### Windows service
```powershell
sc.exe create Soulman binPath= "\"C:\Apps\Soulman\Soulman.exe\"" start= auto
sc.exe start Soulman
```

### Linux systemd (user service)
```bash
# After install-linux.sh, it's already running. Management:
systemctl --user status soulman
systemctl --user restart soulman
journalctl --user -u soulman -f
```

## Project layout
- `src/Soulman/DownloadScanner.cs` — polling, settle detection, tag read, move + clone logic.
- `src/Soulman/SoulmanSettings.cs` — user-configurable settings with platform defaults.
- `src/Soulman/Worker.cs` — background host loop.
- `src/Soulman/InstanceDiscovery.cs` — LAN peer discovery (UDP broadcast/multicast).
- `src/Soulman/TrayHostedService.cs` — tray icon, clone management, move notifications (Windows only).
- `src/Soulman/install-linux.sh` — systemd user service installer.
- `src/Soulman/appsettings*.json` — configuration; add `appsettings.local.json` for machine overrides.
=======
- Optional flags: `-Configuration` (default `Release`), `-PublishProfile` (defaults to `src/Soulman/Properties/PublishProfiles/WinClickOnce.pubxml`), `-ApplicationRevision`, `-CleanOutput` (wipe prior publish folders before building).
- Installer logs to `%TEMP%\soulman_install_<timestamp>.log`; on failure it pauses so you can read the error and log path.
- Installer attempts to add a firewall allow rule named `Soulman LAN Discovery (UDP 45832)` for inbound UDP 45832 on Private/Domain profiles so LAN peers can respond; if blocked or declined, add the rule manually:
  ```powershell
  New-NetFirewallRule -DisplayName "Soulman LAN Discovery (UDP 45832)" -Direction Inbound -Action Allow -Protocol UDP -LocalPort 45832 -Profile Private,Domain
  ```
  Allowing TCP 45833 inbound (Private/Domain) is required for peer sync to connect into this host.
- Installer attempts to add a firewall allow rule named `Soulman LAN Discovery (UDP 45832)` for inbound UDP 45832 on Private/Domain profiles so LAN peers can respond; if blocked or declined, add the rule manually.

## Scan flow & safety
- Enumerates the configured source folders (skips anything that is the destination or sits inside the destination/clone trees) and only looks at supported extensions.
- Tracks observed size/timestamp and only moves a file after it has been stable for `SettledSeconds`.
- Builds target paths from tags; `(Disc #)` is only added for multi-disc albums (disc number > 1 or disc count > 1).
- Moves the organized file into the destination, clones it to any configured clone roots, and logs the result for the last 24 hours.
- Emits warnings when a protected path is encountered so you can adjust sources before anything is moved.

### Tray, clones, and logs
- Tray icon (uses `soulman.ico`) gives:
  - Header shows the current version (ClickOnce version when deployed)
  - Other Soulman Instances shows peers discovered on the local network as `Soulman <version> on <HOSTNAME>`; uses a quick UDP broadcast on port 45832 and a Refresh item to rescan.
    - Discovery sends to directed broadcast, limited broadcast, and multicast `239.255.64.64`; replies also go back to the sender’s source port in case inbound 45832 is blocked.
  - Set Source Folder / Set Destination Folder (persisted to `%LOCALAPPDATA%\Soulman\paths.json`)
  - Add Clone Destination (UNC/network paths allowed) to mirror the organized library
  - Open Source / Destination folders
  - Open Move Log (last 24h, includes clone copies)
  - Run on Startup toggle (adds/removes a shortcut in `%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup`)
  - Update to Latest (downloads the latest GitHub release `soulman_installer.exe`, runs it, and exits this instance)
  - View/clear clone destinations; Exit
- Clone destinations live in `%LOCALAPPDATA%\Soulman\clonefolders.json` and receive a copy of every moved file.

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
- `Install-ClickOnce.ps1` — stages a published payload into `%LOCALAPPDATA%\soulman-clickonce`, clears the ClickOnce cache when available, and launches the packaged `setup.exe` (falls back to `Soulman.application` if needed); used by the GitHub installer, too.
- `Publish-GitHubRelease.ps1` — prompts for the next version (or takes `-Tag`), publishes via MSBuild, bundles the ClickOnce payload into a single self-contained `soulman_installer.exe` that extracts and runs `setup.exe`, and creates a GitHub release (requires `gh auth login`). Artifacts land in `artifacts/github-release/`.

ClickOnce uses the full MSBuild toolchain (not `dotnet msbuild`). If Rider/CLI defaults to `dotnet msbuild`, you'll see `MSB4803`; run the scripts above or point your IDE publish config at `MSBuild.exe`.

## Project layout
- `src/Soulman/DownloadScanner.cs` — polling, settle detection, tag read, move + clone logic.
- `src/Soulman/SoulmanSettings.cs` — user-configurable settings and defaults.
- `src/Soulman/Worker.cs` — background host loop.
- `src/Soulman/TrayHostedService.cs` — tray icon, clone management, move notifications/log UI.
- `src/Soulman/appsettings*.json` — configuration; add `appsettings.local.json` for machine overrides.
>>>>>>> upstream-sync
