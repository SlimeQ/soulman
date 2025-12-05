# Network discovery & firewall notes

## How discovery works
- Soulman instances listen on UDP 45832 and respond to probes.
- Probes are sent to: each active interface's directed broadcast, the limited broadcast (`255.255.255.255`), and multicast `239.255.64.64`.
- Responses include the hostname and version. They are sent to the advertised discovery port **and** back to the sender's source port to survive blocked inbound 45832 on the requester.
- For reliable two-way discovery, each machine should allow inbound UDP 45832 on Private/Domain profiles so it can receive probes.

### Quick smoke test
1) Confirm both machines are on Private network profiles: `Get-NetConnectionProfile`.
2) Ensure both run the current build (tray header should show the same version).
3) Verify firewall rule exists: `Get-NetFirewallRule -DisplayName "Soulman LAN Discovery (UDP 45832)" | Get-NetFirewallPortFilter`.
4) Restart Soulman on both, then use tray → Other Soulman Instances → Refresh on each machine.
5) If still one-way, temporarily turn off Windows Firewall (Private profile) as a sanity check; if it starts working, the rule/policy is blocking discovery.

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
- If Group Policy blocks adds and you see "This setting is managed by your organization," either add the rule via whatever policy controls the firewall (Local Security Policy or domain GPO), or temporarily allow Private inbound UDP 45832. Discovery replies to the sender’s source port, but two-way visibility is more reliable with the rule in place.

## Troubleshooting one-way discovery
- Ensure both machines are on a Private network and running the current build.
- Verify the firewall rule exists on the machine that is not being seen; if group policy blocks adds, you may need to allow the rule manually via local security policy or let a store-bought policy permit UDP 45832.
- Check the installer log in `%TEMP%` for firewall or staging errors.
- If discovery is still one-way, restart Soulman and use the tray "Other Soulman Instances" refresh after confirming the rule and network profile.
- If only one direction works even with firewall off, double-check both machines are on the same subnet and no VPN adapters are grabbing the default route; you can also disable VPN/adapters temporarily and retry discovery.
