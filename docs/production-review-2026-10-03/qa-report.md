# USB Guardian production readiness review

Reviewed 3 October 2026. Verdict: **DO NOT SHIP / REQUEST CHANGES**. The solution and installers build, but confirmed enforcement and durability defects prevent a production-ready verdict. The product cannot currently support a claim that all USB devices block and unblock smoothly.

Scope: Agent enforcement, matching, reconnect and offline behavior; Server authentication, policy persistence and event acknowledgements; Control client and dashboard logic; Notifier; batch deployment scripts; WiX lifecycle authoring; package generation; CI and existing tests. This reviews the current working tree, including existing modified and untracked source files, based on commit `8f76cf428dfc3470ed8a1d722b9f7095879509b6`. It does not certify the old commit or every previously distributed binary. No runtime product source was changed during the review.

## Verification evidence

| Check | Result | Evidence / limitation |
|---|---|---|
| Entire solution Release build | PASS | Zero warnings and errors; [build log](evidence/build.log) |
| Existing regression suite | PASS | 58 passed, 0 failed, 0 skipped; [TRX](evidence/unit-tests.trx) |
| Additional isolated probes | FAIL | 27 checks: 19 pass, 8 fail; [results](evidence/integration-results.json) |
| NuGet vulnerability query, including transitives | PASS | No known vulnerable packages reported for the five projects against NuGet on review date; not a general security certification |
| Self-contained x64 publishing | PASS | Server, Agent, Control and Notifier published into the review's isolated package directory |
| WiX Server / Endpoint / Control linking | PASS | All three 1.1.2 MSIs built with zero warnings and errors; [sizes, signatures and hashes](evidence/packages.json) |
| Artifact signing | NOT READY | Newly generated packages/payload EXEs are unsigned; existing three MSI files in `USBGuardianInstaller/output` are also unsigned |
| Production portable-device WQL | FAIL | Actual compiled query fails with `ManagementStatus.InvalidQuery`; correctly escaped control query succeeds |
| Physical USB disable / enable | NOT VERIFIED | No real Agent was started and no hardware was toggled |
| WPF dashboard / notifier visual interaction | NOT VERIFIED | Native desktop automation unavailable in this session; client HTTP flows and source inspected, no visual pass or screenshots claimed |
| MSI installation, repair, upgrade, rollback, uninstall | NOT VERIFIED | Packaging and authoring reviewed; MSIs were not installed on this workstation |

The probe starts a temporary Server bound only to loopback, with generated credentials and isolated state files. Simulated SignalR agents receive policies without running hardware enforcement. Failure injection locks only those temporary policy/event files. WMI checks execute read-only queries and do not disclose actual device IDs. No services, firewall rules, production registry settings, production ProgramData files, or physical devices were changed. See [probe source](Probe/Program.cs) and [project](Probe/Probe.csproj).

Reproduce the review probes from the solution root after a Release build:

```powershell
dotnet run --project docs/production-review-2026-10-03/Probe/Probe.csproj -c Release -- 'T:\ArcX\USB Blocker'
```

The probe prints PASS/FAIL observations and saves JSON even when it reproduces expected defects; its process exit code alone is not the readiness verdict. Generated local credentials and large package files are excluded by the review directory's `.gitignore`.

## Blocking findings

### R01 — Portable-device query is invalid on Windows [Critical / P0]

Location: [UsbPolicyManager.cs:48](<T:/ArcX/USB Blocker/USBGuardianAgent/UsbPolicyManager.cs:48>), duplicated at [line 685](<T:/ArcX/USB Blocker/USBGuardianAgent/UsbPolicyManager.cs:685>).

The C# string produces `PNPDeviceID LIKE 'USB\VID%'` in WQL, where the literal backslash must itself be escaped. Windows rejects the entire portable-device query with `InvalidQuery`, including the WPD branch. The Agent catches the exception and continues. Phones/MTP devices are consequently omitted from enforcement and reporting through this query.

Reproduction: the probe obtains `RemovableDeviceQueries` from the compiled Agent and executes it with `ManagementObjectSearcher`. Query 1 succeeds; query 2 fails. Replacing the single WQL backslash with two makes query 2 valid and returns one matching device on this workstation. This is a real WMI query failure, not a simulated hardware result.

Fix: correctly escape the backslash at both sites, centralize the query, and add a Windows WMI query-validation test plus a real phone block/unblock acceptance test.

### R02 — Lock commands leave learn mode permissive [High / P1]

Location: [AgentController.cs:155](<T:/ArcX/USB Blocker/USBGuardianServer/Controllers/AgentController.cs:155>), group lock at [line 481](<T:/ArcX/USB Blocker/USBGuardianServer/Controllers/AgentController.cs:481>), all-machine lock at [line 521](<T:/ArcX/USB Blocker/USBGuardianServer/Controllers/AgentController.cs:521>); [UsbPolicyManager.cs:366](<T:/ArcX/USB Blocker/USBGuardianAgent/UsbPolicyManager.cs:366>).

Enable learn mode, then press Lock, Group Lock or Lock All. Each action clears unlock state but leaves `LearnMode=true`. The Agent's decision function allows every removable device while learning, even an explicitly blocked device. The Server still reports a locked policy. The three additional probes reproduce this: `IsUnlocked=false`, `LearnMode=true`, `ShouldAllow=true`.

Fix: have explicit locking cancel learn mode and its expiry in every lock path, with regression coverage. If maintaining learning during a lock is an intentional product decision, the command and dashboard must clearly expose that access is still unrestricted; the current immediate-disable claim is false.

### R03 — Network work can suspend the enforcement loop [High / P1]

Location: [Worker.cs:27](<T:/ArcX/USB Blocker/USBGuardianAgent/Worker.cs:27>), [line 34](<T:/ArcX/USB Blocker/USBGuardianAgent/Worker.cs:34>) and [line 55](<T:/ArcX/USB Blocker/USBGuardianAgent/Worker.cs:55>); [AgentSignalRClient.cs:140](<T:/ArcX/USB Blocker/USBGuardianAgent/AgentSignalRClient.cs:140>).

Startup/connect/register, device reporting and up to 100 event sends all run on the same loop as enforcement, using the service lifetime token rather than short operation deadlines. A device-change signal only wakes the subsequent wait; it cannot interrupt a pending network operation. Thus a stalled server can delay handling new arrivals and timed-expiry enforcement beyond the documented five-second fallback.

Reproduction: a local TCP listener accepts the connection but does not answer negotiation. A SignalR connection configured like the Agent is still awaiting `StartAsync` after 5.5 seconds. The probe cancels it safely. This proves the network wait can exceed the enforcement budget; actual USB timing remains untested.

Fix: separate enforcement/event monitoring from connection and event-delivery work, and bound network operations. Validate the maximum scan interval during stalled negotiation, reconnect and a large event backlog.

### R04 — Failed security-event writes are acknowledged and lost [High / P1]

Location: [EventStore.cs:105](<T:/ArcX/USB Blocker/USBGuardianServer/EventStore.cs:105>), [AgentHub.cs:231](<T:/ArcX/USB Blocker/USBGuardianServer/AgentHub.cs:231>), [Worker.cs:64](<T:/ArcX/USB Blocker/USBGuardianAgent/Worker.cs:64>).

`EventStore.Append` returns false for both duplicate events and failed writes. `ReportEventV2` nevertheless completes successfully. The Agent treats successful completion as durable acceptance and deletes the outbox entry. Disk-full, ACL or sharing failures can permanently remove the only copy of a blocked-device event.

Reproduction: exclusively lock the isolated endpoint's event log and invoke `ReportEventV2`. The invocation succeeds, but the event is absent after releasing the lock. Successful deduplication is separately verified.

Fix: return distinct persisted/already-present/failed receipts and acknowledge the outbox only for durable acceptance. Restrict legacy-method fallback to an actual unsupported-method error, since the current client downgrades on any `HubException`.

### R05 — USBSTOR-only storage detection misses the UASP path [High / P1; coverage gap]

Location: [UsbPolicyManager.cs:40](<T:/ArcX/USB Blocker/USBGuardianAgent/UsbPolicyManager.cs:40>) and [line 676](<T:/ArcX/USB Blocker/USBGuardianAgent/UsbPolicyManager.cs:676>).

Storage detection exclusively selects `USBSTOR%`; it does not traverse USB ancestry, inspect storage bus metadata or enumerate the UASP driver path. The other query targets portable-device classes/names. Microsoft documents both `Usbstor.sys` and `Uaspstor.sys` as mass-storage drivers, with UASP in the SCSIAdapter setup class. A UASP enclosure whose node is a SCSI disk or a USB UASP adapter and whose name does not match the phone/camera heuristics is outside these filters. This is a source-backed coverage inference, not a physically demonstrated bypass on this workstation. [Microsoft USB class-driver documentation](https://learn.microsoft.com/en-us/windows-hardware/drivers/usbcon/supported-usb-classes).

Fix: identify storage by bus/driver metadata and device ancestry, then operate on the exact safe storage/function instance. Test UASP SSDs, external HDDs and bridges. Preserve the prohibition on disabling shared hubs/controllers or ordinary input devices.

### R06 — Dashboard conflates policy intent with enforcement success [High / P1]

Location: [UsbPolicyManager.cs:714](<T:/ArcX/USB Blocker/USBGuardianAgent/UsbPolicyManager.cs:714>), [AgentController.cs:98](<T:/ArcX/USB Blocker/USBGuardianServer/Controllers/AgentController.cs:98>), [MainWindow.xaml.cs:44](<T:/ArcX/USB Blocker/USBGuardianControl/MainWindow.xaml.cs:44>).

`IsAllowed` describes the policy decision, not the pnputil result. The Control's BLOCKED and CONNECTED labels depend solely on that flag. A running unauthorized device after `BlockFailed` is still labeled BLOCKED; an allowed but disabled device can be labeled CONNECTED. Machine LOCKED reflects the Server's desired policy and has no applied-policy acknowledgement. The API also returns success for saved policy even when live delivery fails.

Fix: report observed device state, last enforcement error and applied policy revision separately. Show pending/offline/failed states until endpoint confirmation and stop claiming immediate success from a policy save alone.

### R07 — A fleet token can impersonate any endpoint [High / P1; security architecture]

Location: [AgentHub.cs:36](<T:/ArcX/USB Blocker/USBGuardianServer/AgentHub.cs:36>) and [line 56](<T:/ArcX/USB Blocker/USBGuardianServer/AgentHub.cs:56>).

Precondition: a client possesses the shared Agent token. Registration accepts any machine ID with that token and removes the prior connection for the claimed identity. There is no per-endpoint credential binding. A compromised token holder can replace another endpoint's session, submit fake inventory/events and divert subsequent policy delivery from the real endpoint. No admin password is required for this takeover; the fleet token is required.

Reproduction: a second simulated client registers the first client's machine ID using the same token; it receives the policy, and the original client's updates are then rejected as unregistered.

Fix: use individual endpoint credentials or certificates bound to the enrolled machine identity, with explicit replacement/enrollment controls and revocation.

### R08 — Plaintext deployment exposes privileged control traffic [High / P1; production deployment]

Location: [appsettings.json](<T:/ArcX/USB Blocker/USBGuardianServer/appsettings.json>), [ServerClient.cs:192](<T:/ArcX/USB Blocker/USBGuardianControl/ServerClient.cs:180>), [AgentSignalRClient.cs:265](<T:/ArcX/USB Blocker/USBGuardianAgent/AgentSignalRClient.cs:260>).

The shipped configuration enables HTTP on all interfaces and disables HTTPS. The admin password is sent in an HTTP header; agents submit the fleet token over the same transport. On an observable or actively intercepted LAN, those credentials/control messages lack transport confidentiality and integrity. The Agent runs as LocalSystem and accepts remote reboot/shutdown commands, so server authenticity matters. This is a configuration/code finding; no network interception was performed.

Fix: ship a documented, tested production profile using trusted HTTPS with HTTP disabled, or enforce equivalent authenticated network protection. Validate certificate trust, hostname matching, renewal and restricted firewall exposure. Existing LAN-compatible HTTP support is a repository requirement, so the review does not silently change it.

### R09 — Endpoint state-write failures can break later recovery [High / P1; source-confirmed]

Location: [UsbPolicyManager.cs:338](<T:/ArcX/USB Blocker/USBGuardianAgent/UsbPolicyManager.cs:338>), [line 932](<T:/ArcX/USB Blocker/USBGuardianAgent/UsbPolicyManager.cs:932>) and [line 900](<T:/ArcX/USB Blocker/USBGuardianAgent/UsbPolicyManager.cs:900>).

A successful disable is followed by an in-memory ownership update and a best-effort file write. If persistence fails, the error is logged, but there is no dirty-state retry; an already-disabled device produces no fresh action. After restart, its ownership can be missing, and unlock/uninstall will not restore it. There is also a crash window between device disable and ownership persistence.

Similarly, a failed policy-cache write can leave an old indefinite-unlock policy on disk after a new Lock was enforced in memory. Restarting offline can reload that older permissive policy. These failure paths are established by source inspection; hardware/disk-failure end-to-end reproduction was not attempted because the Agent uses fixed production state paths.

Fix: make ownership and policy persistence an explicit recoverable state transition, retain dirty writes for retries, and use a durable journal for device actions. Validate crash recovery and disk-full behavior before claiming safe uninstall/offline enforcement.

## Medium findings

### R10 — Failed policy save leaves changed live Server state [Medium / P2]

Location: [PolicyStore.cs:104](<T:/ArcX/USB Blocker/USBGuardianServer/PolicyStore.cs:104>).

The store assigns the new policy into `_policies` before persistence. A failed write throws without restoring the prior value. Reproduction: hold the isolated policy file open exclusively and request Unlock. The API returns 500, no unlock push is sent, but the next agents query reports the machine OPEN. A later write can persist a change whose original request failed.

Fix: persist a candidate snapshot before publishing it in memory, or restore the old state on failure; add a save-failure regression test.

### R11 — Async device refresh can display another selected machine's devices [Medium / P2; source-confirmed]

Location: [MainWindow.xaml.cs:229](<T:/ArcX/USB Blocker/USBGuardianControl/MainWindow.xaml.cs:229>) and [line 241](<T:/ArcX/USB Blocker/USBGuardianControl/MainWindow.xaml.cs:241>).

Selecting A starts its device request; selecting B starts another before A completes. Neither response checks whether its machine is still selected. If A completes last, its devices replace B's list, while device actions use the current `_selectedMachineId` B. This can apply A's device-ID rule to B. The timer overlap guard does not guard selection requests. This was found in source; the WPF click sequence has not been visually reproduced.

Fix: capture the requested machine identity and use request versions/cancellation; publish results only if the selection still matches.

## What works, and what “all USB” means

The isolated tests validate authentication rejection, loopback password rotation, authenticated registration, exact PNP-ID route round trips through the real Control client, allow/block list updates, timed-unlock expiry delivery, ordinary locking outside learn mode, optical/BadUSB flag delivery, invalid duration rejection, event deduplication, offline inventory retention and policy replay on reconnect. Existing tests verify pure matching, planner ownership decisions, persisted expiry and installer/script authoring invariants. Those are useful foundations, but most do not execute WMI or pnputil.

| Device / feature | Current design | Readiness |
|---|---|---|
| Conventional USBSTOR flash drives / disks | Default deny when locked, allow/block rules, unlock | Policy paths tested; physical disable/restore pending |
| UASP SSDs / HDD enclosures | No reliable classification path | R05; test and extend detection |
| Phone/MTP/WPD | Intended enforcement | R01 query is currently broken |
| Hubs / host controllers | Inventory only | Intentionally never blocked |
| Ordinary keyboard / mouse | Protected by normal enforcement guard | Intentionally not an all-USB blocker |
| New keyboards with BadUSB enabled | Optional baseline-based detection/blocking | Physical acceptance required; this is not proof of preventing every injection |
| Printers, audio, serial adapters, USB Ethernet, vendor interfaces | No general block-all implementation | Outside current normal removable-device enforcement |
| Optical / Bluetooth / Wi-Fi | Optional class/heuristic sector policies | Flags tested; device behavior pending |
| Per-device block while globally unlocked | Saved rule, overridden by unlocked sector | Documented behavior; block becomes effective when relocked |

Normal scanning occurs after device enumeration, with a 250 ms debounce and periodic fallback. There is no demonstrated pre-mount/pre-I/O denial guarantee. Validate whether an unauthorized device can be used before it is disabled; do not market this as zero-window DLP or complete BadUSB prevention.

## Security and release assessment

Assets/trust boundaries: admin credentials and requests across LAN HTTP; endpoint identity and token across SignalR; remote privileged commands to LocalSystem; policy/cache/ownership/events on disk; service binaries and MSI upgrades; alert files consumed in user sessions. Existing positive controls include BCrypt password hashing, loopback restriction for initial rotation, explicit rejection of empty AgentSecret by default, fixed-time token comparison, per-connection claimed-ID checks, admin API rate limiting, bounded input messages, protected registry/file settings, direct structured pnputil arguments, timeouts and retry backoff.

Overall security risk: **High for the default network deployment**, chiefly R07/R08 and misleading enforcement state. No unauthenticated arbitrary-code-execution finding is claimed. The NuGet result covers advisory matches, not all possible application defects. Sign the actual distribution with a trusted certificate; an SHA-256 checksum alone does not establish a publisher's identity.

Operational compatibility also needs an explicit minimum Windows version. Microsoft states `/disable-device` and `/enable-device` require Windows 10 version 2004 or later; currently the MSI authoring lacks that specific capability check. [PnPUtil command documentation](https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/pnputil-command-syntax).

.NET 8 remains supported on the review date but ends support on **10 November 2026**. Plan a supported runtime migration before production maintenance relies on it; self-contained packages require rebuilding to carry updated runtimes. [Microsoft .NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core).

## Required acceptance gate before release

1. Resolve R01/R02 first, then decouple enforcement timing, correct event receipts and implement truthful applied-state reporting. Add regression coverage for the reproduced failures. Address identity/transport and endpoint recovery durability before fleet rollout.
2. On disposable Windows test endpoints with LocalSystem Agent, test flash drives, external HDDs, UASP SSDs, Android MTP, iPhone/PTP and representative hubs/docks while retaining keyboard/mouse usability. Prove allow/block, unplug/replug and unlock/relock behavior.
3. Measure first-arrival access and enforcement latency during server outage, stalled negotiation, missed WMI events, event backlog and timed-unlock expiry. Test service restart/reboot during unlock and offline learn-mode transitions.
4. Exercise optical, Bluetooth, Wi-Fi and optional BadUSB on approved hardware, including loss of the management connection, baseline behavior and restoration. Maintain out-of-band recovery for network sector tests.
5. Validate standard-user notifier delivery at logon, repeat alerts, dwell/autoclose, multi-session behavior, restart and folder-watch failure. Drive the actual dashboard, rapid selection switching, auth failure, offline actions, server reconnect and DPI/layout behavior.
6. Install, upgrade, repair, roll back and uninstall signed MSIs on clean test VMs/endpoints. Verify service identities/ACLs, token secrecy, config preservation, user notifier startup and recovery of Agent-owned disabled devices. Include failed pnputil, disk-full and crash-injection scenarios.

Highest-priority fixes: portable WQL, lock/learn precedence, independent enforcement scheduling, durable event receipts, and accurate device coverage/applied-state reporting. Production approval requires those fixes plus the hardware and deployment gate; compilation alone is insufficient.
