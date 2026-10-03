# USB Guardian Release Acceptance Checklist

Release: {{VERSION}}  
Tester: ____________________  
Date: ____________________  
Server OS/build: ____________________  
Endpoint OS/build and hardware model: ____________________

Record evidence for every failure: timestamp, machine name, Event Viewer entry,
dashboard history, and relevant `pnputil` exit result. Never attach Agent tokens
or password material.

## Package Integrity

- [ ] If distributed as a ZIP, its SHA-256 matches the separately supplied value.
- [ ] Each MSI matches `SHA256SUMS.txt`.
- [ ] Record whether this is an unsigned acceptance candidate or a signed production build.
- [ ] For production, all four payload EXEs and all three MSIs pass Authenticode verification with the expected trusted publisher; no Unknown publisher packages are approved.

## Server

- [ ] Fresh Server MSI installation succeeds.
- [ ] `USB Guardian Server` runs as LocalSystem and is set to Automatic.
- [ ] TCP 5050 is listening.
- [ ] `http://127.0.0.1:5050/health` returns `status: healthy`.
- [ ] Configure a trusted HTTPS certificate, private-key access for LocalSystem, and the matching firewall port.
- [ ] HTTPS succeeds from each endpoint using the certificate's DNS name; invalid/untrusted certificates fail.
- [ ] Production has `Security:RequireHttps=true`, `Security:AllowLegacySharedAgentToken=false`, and `Security:AllowUnauthenticatedAgents=false`.
- [ ] Remote admin API and Agent hub calls over HTTP are rejected after production configuration.
- [ ] Default `admin` works only locally.
- [ ] First password rotation succeeds locally.
- [ ] Remote dashboard access succeeds with the rotated password over trusted HTTPS.
- [ ] Rotate existing administrator passwords after upgrade to migrate legacy BCrypt hashes.

## Endpoint And Dashboard

- [ ] Fresh Endpoint MSI installation succeeds with trusted HTTPS URL, explicit MachineId and a unique enrolled token.
- [ ] Reusing another endpoint's token/identity is rejected; rotation and revocation disconnect old sessions.
- [ ] Existing JSON precedence is handled using the migration guide; do not discard recovery state.
- [ ] `USB Guardian Agent` runs as LocalSystem and is set to Automatic.
- [ ] Endpoint appears Online within approximately ten seconds.
- [ ] Endpoint remains registered after Agent and Server service restarts.
- [ ] Dashboard distinguishes pending/stale/failed enforcement from a fresh verified receipt of the requested policy revision.
- [ ] Incorrect Agent token is rejected and recovery works after correction.
- [ ] Notifier starts after user logon.
- [ ] Control dashboard install and Start menu shortcut work.

## Removable Device Enforcement

- [ ] Keyboard and mouse remain usable throughout all tests.
- [ ] Webcam, audio, hubs and composite dock functions remain usable under normal removable-device policy.
- [ ] Unauthorized USB storage is disabled while locked.
- [ ] Test USBSTOR and UASP drives plus WPD/MTP phones in their available USB modes; record exact device IDs and observed behavior.
- [ ] Hub and controller rows remain display-only.
- [ ] Exact-device Allow keeps only the approved device usable.
- [ ] Block reapplies after unplug/reinsert without repeated reconnect loops.
- [ ] Unlock restores only devices disabled by USB Guardian.
- [ ] Timed unlock relocks at expiry, including across Agent restart.
- [ ] Offline cached policy remains fail-closed.
- [ ] Measure first-arrival access and block latency; no universal USB or zero-window data-loss-prevention claim is approved by this test.
- [ ] Repeated scans do not toggle an already-correct device or produce console/reconnect loops.

## Additional Controls

- [ ] Learn mode captures approved devices and stops at expiry.
- [ ] Learn mode fails closed during Server disconnection.
- [ ] Optical sector behavior is validated where supported.
- [ ] Bluetooth sector behavior is validated where supported.
- [ ] Wi-Fi behavior is tested only with wired/local recovery available.
- [ ] BadUSB is tested with approved spare input devices.
- [ ] Full-screen block alert and event history are correct.
- [ ] Two simultaneous interactive sessions receive alerts independently; test logon startup, busy files, two-second dismissal dwell and auto-close.
- [ ] Reload, reboot, and shutdown confirmations behave as documented.

## Lifecycle

- [ ] MSI repair preserves Server and Endpoint configuration.
- [ ] Major upgrade preserves configuration.
- [ ] Failed install/upgrade rollback preserves configuration, ownership journal and usable recovery tools.
- [ ] Downgrade is rejected.
- [ ] Endpoint uninstall restores Agent-managed devices.
- [ ] Test disconnected devices and failed restoration; preserve failed IDs and retain the Agent executable for an elevated recovery retry. Uninstall attempts recovery best-effort.
- [ ] Services, notifier startup, firewall rule, and shortcuts are removed.
- [ ] Reinstall after uninstall succeeds.
- [ ] As LocalSystem, reject untrusted pre-created files and file/directory links; migrated runtime files are owned by SYSTEM or Administrators.
- [ ] Standard users can read alerts but cannot replace runtime files or directories or read endpoint credentials.
- [ ] Disk-full, locked-file, process-crash and power-loss tests preserve policy/event/recovery state without false acknowledgements.
- [ ] Record a supported runtime maintenance plan before .NET 8 support ends on 10 November 2026.

## Result

- [ ] PASS — approved for the next release gate.
- [ ] FAIL — blocked pending the defects listed below.

Defects/evidence references:

______________________________________________________________________________

______________________________________________________________________________
