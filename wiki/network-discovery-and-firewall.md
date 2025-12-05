# Network discovery & firewall notes

## How discovery works
- Soulman instances listen on UDP 45832 and respond to probes.
- Probes are sent to: each active interface's directed broadcast, the limited broadcast (`255.255.255.255`), and multicast `239.255.64.64`.
- Responses include the hostname and version. They are sent to the advertised discovery port **and** back to the sender's source port to survive blocked inbound 45832 on the requester.
- For reliable two-way discovery, each machine should allow inbound UDP 45832 on Private/Domain profiles so it can receive probes.

## Installer behavior
- The installer logs to `%TEMP%\soulman_install_<timestamp>.log`. On errors it prints the failure, shows the log path, and waits for Enter before closing.
- It best-effort adds a Windows Firewall rule named `Soulman LAN Discovery (UDP 45832)` allowing inbound UDP 45832 on Private/Domain profiles. If policy blocks rule creation you'll see "This setting is managed by your organization" or a failure in the log.
- "mage.exe not found" is a warning that ClickOnce cache clearing was skipped; it does **not** stop the install.

## Manual firewall setup / verification
- Add the rule from an elevated PowerShell if the installer was blocked:
  ```powershell
  New-NetFirewallRule -DisplayName "Soulman LAN Discovery (UDP 45832)" -Direction Inbound -Action Allow -Protocol UDP -LocalPort 45832 -Profile Private,Domain
  ```
- Confirm the rule:
  ```powershell
  Get-NetFirewallRule -DisplayName "Soulman LAN Discovery (UDP 45832)" | Get-NetFirewallPortFilter
  ```

## Troubleshooting one-way discovery
- Ensure both machines are on a Private network and running the current build.
- Verify the firewall rule exists on the machine that is not being seen; if group policy blocks adds, you may need to allow the rule manually via local security policy or let a store-bought policy permit UDP 45832.
- Check the installer log in `%TEMP%` for firewall or staging errors.
- If discovery is still one-way, restart Soulman and use the tray "Other Soulman Instances" refresh after confirming the rule and network profile.
