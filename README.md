# Soulman

**Soulman** is a distributed media library manager and Soulseek companion. It moves files from your download folders (Soulseek, Torrent, etc.) to your organized media libraries (Music, Movies, TV), and syncs them across your local network nodes.

## Features

- **Cross-Platform:** Runs on Windows (Tray App) and Linux (Headless Service).
- **Media Sorting:** Automatically detects and sorts:
  - 🎵 **Music** (Artist/Album/Title) via ID3 tags
  - 🎬 **Movies** (Title (Year)) via filename
  - 📺 **TV Shows** (Show/Season/Episode) via filename
  - 📝 **Subtitles** (Sidecar .srt/.vtt moved with video)
- **Distributed Nodes:** Run Soulman on multiple machines.
  - **Downloader Node:** Running Soulseek/slskd + Soulman (Source).
  - **Library Node:** Running Soulman (Destination).
  - **Peer Sync (Coming Soon):** Automatically sync received files to other nodes.

## Installation

### Linux
Run the one-line installer to clone and build:
```bash
curl -sL https://raw.githubusercontent.com/SlimeQ/soulman/feature/cross-platform/install.sh | bash
```

Manual:
```bash
git clone https://github.com/SlimeQ/soulman.git
cd soulman
dotnet build src/Soulman.Linux/
# Configure
dotnet run --project src/Soulman.Setup/
# Run
dotnet run --project src/Soulman.Linux/
```

### Windows
Run the PowerShell installer:
```powershell
irm https://raw.githubusercontent.com/SlimeQ/soulman/feature/cross-platform/install.ps1 | iex
```

Manual:
1. Clone the repo.
2. Build `src/Soulman.Windows/Soulman.Windows.csproj`.
3. Run `Soulman.Windows.exe`.

## Configuration

Run the setup wizard to configure your node:
```bash
dotnet run --project src/Soulman.Setup/
```

This will generate a `soulman.json` config file.

### Key Settings
- **Node Name:** Unique name for this machine.
- **Node Behavior:**
  - `DownloadFromSoulseek`: This node runs Soulseek/slskd.
  - `ReceiveFromPeers`: This node accepts files from other Soulman nodes.
- **Gathering Rules:** Enable/Disable Music, Movies, TV gathering.
- **Library Paths:** Where to move files to.

## Project Structure

- `src/Soulman.Core`: Shared logic (Scanner, Settings, Network).
- `src/Soulman.Windows`: Windows Tray Application (WinForms).
- `src/Soulman.Linux`: Linux Systemd Service (Console).
- `src/Soulman.Setup`: TUI Configuration Wizard (Spectre.Console).

## Development

Requirements: .NET 9.0 SDK.

Build all:
```bash
dotnet build
```
