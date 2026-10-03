# USB-Lock-RP Reference Comparison

The local `MY_USB-Lock-RP200v13888` package contains 2019 Windows installers and
2024 product manuals. It does not contain source code. The review was therefore
limited to documented behavior, installer metadata, and static executable
metadata. The installers were not executed.

## Patterns Adopted

| Reference behavior | USB Guardian implementation |
|---|---|
| Endpoint service enforces policy without the control UI | Agent caches the latest policy and enforces it offline |
| Automatic authorization has a fixed window | Learn mode has a persisted expiry, a 48-hour maximum, and fails closed when disconnected |
| Exact ID and vendor/model authorization | `DeviceMatcher` supports exact instance IDs and VID/PID or VEN/PROD tokens |
| Group auto-enforcement reaches returning machines | Group and global operations update every known policy, including offline endpoints |
| Clients remain visible after disconnect | ACL-protected machine inventory persists identity, address, last-seen time, and last device count |
| Separate removable, optical, Bluetooth, and Wi-Fi sectors | Independent persisted sector flags are pushed to the agent |
| Insertion alerts and per-client history | JSONL event history, WPF history view, CEF, and optional SMTP |
| BadUSB keyboard-change assessment | Baseline keyboards stay trusted; new unapproved keyboard arrivals can be reported and targeted |
| Client-side full-screen warning | A user-session notifier consumes atomic alert files from ProgramData |
| MSI/GPO-friendly client configuration | Separate self-contained Server, Endpoint, and Control WiX packages support silent properties, protected machine configuration, upgrades, and optional signing |

## Reliability Improvements Beyond The Reference

- Normal enforcement never disables hubs or host controllers. This avoids
  collateral keyboard, mouse, camera, and network loss.
- `pnputil.exe` is invoked directly with an argument list. Exit code and timeout
  are checked before an action is reported as successful.
- Hardware passes are serialized so the worker and a SignalR policy push cannot
  race each other.
- Initial SignalR failures and exhausted reconnect cycles are retried by the
  worker.
- WMI device-change events trigger enforcement after a short PnP debounce while
  a periodic scan remains as a missed-event fallback.
- Security events remain in an ACL-protected disk outbox while the Server is
  offline, retain their original occurrence time, and are deduplicated by ID.
- The Agent keeps an ownership ledger and restores only device nodes that it
  successfully disabled, including during Endpoint uninstall.
- Failed hardware actions use bounded retry backoff instead of invoking
  `pnputil` every five seconds.
- Policy updates are cloned, normalized, and written atomically under one lock.
- Offline endpoints remain visible without exposing stale device rows as live
  data; live-only remote commands are disabled until the agent reconnects.
- Timed unlock expiry is part of persisted policy instead of a transient message.
- The admin client throws on non-success HTTP responses instead of presenting
  failed changes as successful.

## Deliberately Not Copied

The reference describes driver-load prevention, forced dismount/eject, desktop
lockdown until device removal, file-transfer interception, transparent USB-file
encryption, remote uninstall, and broad system/process inventory.

Those behaviors require a signed filter-driver and threat model, or carry a high
risk of data loss, denial of service, privacy exposure, or remote lockout. They
should not be recreated with ad hoc WMI, filesystem watchers, or process killing.
Each needs a separate design, security review, recovery path, and Windows
hardware qualification.

## Remaining Product Gaps

- Windows execution of the MSI install/repair/major-upgrade/uninstall matrix
- Production certificate use in the implemented signing pipeline and enterprise
  certificate/TLS provisioning
- On-hardware acceptance coverage across Windows versions and USB controller
  vendors
