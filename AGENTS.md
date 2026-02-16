# Soulman - Distributed Media Library

Soulman is a cross-platform tool for managing media files (Music, Movies, TV) from Soulseek and other sources.

## Core Logic

- **Soulman.Core**: Shared logic for file moving, scanning, and settings.
- **Soulman.Windows**: Tray app for Windows.
- **Soulman.Linux**: Systemd service for Linux.

## Modes

- **Downloader Node**: Runs Soulseek/slskd. Sources files.
- **Library Node**: Receives files. Destination for Music/Movies/TV.
- **Peer Sync**: Future feature to push files between nodes.

## Media Types

- **Music**: Artist/Album/Title (ID3)
- **Movies**: Title (Year)
- **TV**: Show/Season XX/SXXEXX
- **Subtitles**: .srt, .vtt (sidecar)
