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
