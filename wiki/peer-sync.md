# Peer sync

## What it does
- Each instance exposes a sync server (TCP, default port 45833; falls back to a dynamic port if 45833 is taken) that serves files from the configured `DestinationPath`.
- A background `SyncWorker` runs every ~5 minutes:
  - Discovers peers over UDP 45832 (see `network-discovery-and-firewall.md`).
  - Connects to each peer’s sync port and requests a file list.
  - Downloads any files that **don’t already exist locally**, preserving relative paths under the local `DestinationPath`.
  - Logs each pulled file to the move log (`MoveLogStore`) with a `Peer://<HOST>/<path>` source and publishes a move notification.
- Sync logs:
  - `SyncWorker` logs when discovery starts/ends and when it syncs each peer.
  - `SyncClient` logs per-file download start/completion, skips existing files, and logs partial/timeout failures.
  - `SyncServer` logs when it serves a file and when a send fails.
- There is no overwrite/merge logic yet; existing files are skipped.
- Per-chunk network reads have a 30s timeout to avoid hanging on stalled transfers; large files will keep the connection alive as long as data is flowing.

## Networking requirements
- Discovery: UDP 45832 inbound/outbound (Private/Domain). Replies also go to the requester’s source port, but having the inbound rule is more reliable.
- Sync: TCP inbound on the peer’s sync port (tries 45833, may pick another ephemeral port on failure). To keep it predictable, allow TCP 45833 inbound on Private/Domain and ensure nothing else binds it.
- Both peers must have `DestinationPath` set to a reachable location; the server only serves files under that path.

## Operational notes
- Schedule: fixed 5-minute interval (no user knob yet).
- Filtering: only files allowed by `SoulmanSettings.IsSupportedFile` are listed/served.
- Safety: server blocks traversal (`..`) and enforces that served paths stay under `DestinationPath`.
- Resumes: not supported; failed downloads are retried on the next sync cycle.
- Progress: `TransferProgressBroker` fires per-chunk progress and completion events.

## Troubleshooting
- Ensure both machines are on a Private network profile and running the same build.
- Verify firewall:
  ```powershell
  Get-NetFirewallRule -DisplayName "Soulman LAN Discovery (UDP 45832)" | Get-NetFirewallPortFilter
  New-NetFirewallRule -DisplayName "Soulman Sync (TCP 45833)" -Direction Inbound -Action Allow -Protocol TCP -LocalPort 45833 -Profile Private,Domain
  ```
- If discovery works but sync fails/times out:
  - Check that the peer’s sync server is listening: `Get-NetTCPConnection -LocalPort 45833`.
  - Large transfers: if you see `Read timed out`, ensure the network isn’t stalling; per-chunk timeout is 30s.
  - If 45833 is occupied, the server chooses a dynamic port; check logs for “SyncServer started on port …” and allow that port inbound.
