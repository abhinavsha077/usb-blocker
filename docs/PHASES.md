# USB Guardian → Master Tool — Phased Implementation Plan

Goal: evolve USB Guardian from a LAN USB lock/unlock tool into a production-ready
endpoint USB-control product, closing the gaps identified against the reference
product (USB-Lock-RP v13.89), **using the existing stack** (.NET 8, ASP.NET Core,
SignalR, WPF, WMI + `pnputil`). No architecture rewrite.

Status legend: ☐ not started · ◐ in progress · ☑ done

---

## Reference product intel (competitive analysis summary)

**USB-Lock-RP** (Advanced Systems International, in market since 2004, ~9000 endpoints/control):
- **Architecture:** 2-tier — Control (`usblockrp.exe`, admin GUI **+** server, default port 3100)
  and Client (`ssrvc.exe`, hidden system service in `ProgramFiles(x86)\ssrvc\`).
  Plus `usblockrpsvc` for 24/7 "system-mode."
- **Deployment:** Client & Control are **Inno Setup 5.5.7** EXEs. GPO deployment uses an
  **EXEMSI 9.0.30-wrapped MSI** that runs the client silently:
  `/SP- /VERYSILENT /SUPPRESSMSGBOXES /NOCANCEL /LOG /NORESTART /IP <ip> /PORT <port>`
  — i.e. the Control address is injected into the client at install time.
- **Feature set:** sector-based blocking (removable / CD-DVD / Bluetooth-IrDA / WiFi),
  VID/PID + full-ID whitelisting, machine groups, Auto-Authorization "learn" mode,
  full-screen endpoint lockdown, per-group master password, event history + color-coded
  alerts, file-to-USB monitoring, AES-256 USB encryption, BadUSB/keystroke-injection
  defense, email alerts, CEF/SIEM logs, scheduled reports, remote reboot/reload/uninstall,
  discovery (sysinfo/software/processes), license recovery, bulk client update.

**USB Guardian today (baseline):** per-machine lock/unlock, **timed unlock (we win here)**,
exact-full-instance-ID allow/block, coarse hub-level `pnputil` enforcement + USBSTOR/MTP
eject, live 5s device snapshot, single WPF console. Gaps: everything else above.

---

## Guiding principles

1. **Same tech, no rewrite.** Extend `UsbPolicyManager` (WMI/pnputil), the SignalR hub,
   the REST controller, `PolicyStore`, and the WPF Control.
2. **Clean-room.** Behavior modeled from USB-Lock-RP's public docs + standard Windows APIs.
   No decompilation of their binaries.
3. **Every phase must build green and be verified** before the next starts.
4. **Backward-compatible data.** Extend the JSON models additively; migrate on load.

---

## Phase P0 — Safety & security hardening  ☑ (code complete; P0.1 needs on-hardware test)

The two items here are the ones that are actively dangerous today.

- ☑ **P0.1 HID carve-out (correctness bug).** Enforcement rewritten to state-aware,
  per-device toggling of removable storage (`USBSTOR%`) + MTP only. Hubs/controllers are
  no longer disabled for enforcement; added `IsHumanInterfaceDevice()` guard. Removed
  `_prevAnyBlocked` gate + `ForceApplyToStorageDevices`.
  - Files: `USBGuardianAgent/UsbPolicyManager.cs`
  - ⚠ Requires on-hardware verification (LocalSystem + real USB): confirm blocking a stick
    ejects it but the keyboard/mouse survive.
- ☑ **P0.2 Enforce agent identity.** Agent sends `AgentToken` on `RegisterAgent`; hub
  validates via `ServerConfigManager.ValidateAgentToken`; unknown token → `Context.Abort()`.
  Empty `AgentSecret` rejects registrations unless an explicit development-only
  unauthenticated switch is configured.
  - Files: `AgentSignalRClient.cs`, `AgentHub.cs`, `ServerConfigManager.cs`.
- ☑ **P0.3 Kill the default password.** `passwordIsDefault` tracked; remote
  (non-loopback) API access and first-password rotation are refused while default;
  change-password flips the flag. Added `GET /api/status` and per-IP API limits.
  Verified at runtime.
  - Files: `ServerConfigManager.cs`, `AgentController.cs`.
- ☑ **P0.4 Transport.** Configurable CORS allow-list (`Cors:AllowedOrigins`) + opt-in HTTPS
  (`Https:Enabled`/`Https:Port`); HTTP 5050 preserved. Non-breaking.
  - Files: `USBGuardianServer/Program.cs`.
- ◐ **P0.5 Verify:** ☑ solution builds green; ☑ status/auth/loopback-write verified at runtime;
  ☐ on-hardware USB block-does-not-kill-keyboard test (needs the deployed agent).

## Phase P1 — Core capability parity (high value, cheap on this stack)  ☑

- ☑ **P1.1 Event history + persistence.** `EventStore` (append-only per-machine JSONL,
  self-trimming) + a durable Agent outbox with stable event IDs + admin-action logging in the
  controller + `GET /api/agents/{id}/history` + a colour-coded WPF `HistoryWindow` (📜 button).
  Built; **integration-verified** (insertion logged once despite repeated pushes; admin Lock
  logged; persisted; newest-first).
  - Files: `EventStore.cs`, `AgentHub.cs`, `AgentController.cs`, `Program.cs`,
    `ServerClient.cs`, `HistoryWindow.xaml(.cs)`, `MainWindow.xaml(.cs)`.
- ☑ **P1.2 VID/PID vendor-model whitelisting.** `DeviceMatcher` supports full-instance match
  and vendor/model (VID/PID and VEN/PROD) tokens. Wired into agent enforcement + display.
  Built; **14/14 truth-table tests pass** (incl. safety cases: full-id ≠ other serial,
  token ≠ other model).
  - Files: `USBGuardianAgent/DeviceMatcher.cs`, `UsbPolicyManager.cs`.
- ☑ **P1.3 Auto-Authorization "learn mode".** Per-machine toggle; while ON the agent allows
  all removable/MTP and the server captures each connected device into the allow-list;
  auto-expires (default 8h) → protection effective. Full stack incl. WPF "🎓 Learn Mode"
  button. Built; **integration-verified** (enable → capture → persist → ApplyPolicy push).
  - Files: `MachinePolicies` (both), `UsbPolicyManager`, `AgentHub`, `AgentController`,
    `ServerClient`, `MainWindow.xaml(.cs)`.
- ☑ **P1.4 Machine groups.** `MachinePolicies.Group` + `PolicyStore.Snapshot()`; REST
  assign (`/agents/{id}/group`), list (`/groups`), and group-scoped `lock`/`unlock`/`learnmode`;
  agent list carries the group; WPF group controls (assign + Lock/Unlock Group). Built;
  **integration-verified** (assign → `/groups` counts → group-lock propagated to the member).
  - Files: `PolicyStore.cs`, `AgentController.cs`, `ServerClient.cs`, `MainWindow.xaml(.cs)`.
- ☑ **P1.5 Persistent endpoint inventory.** Server-side identity, address,
  last-seen time, and last device count survive disconnects and restarts. The
  dashboard keeps offline endpoints manageable while suppressing stale device
  rows and disabling live-only remote commands.
  - Files: `MachineInventoryStore.cs`, `AgentHub.cs`, `AgentController.cs`,
    `ServerClient.cs`, `MainWindow.xaml(.cs)`.
- ☑ **P1.6 Verify:** VID/PID unit-verified; learn-mode + P0.2 token integration-verified;
  event history integration-verified; groups integration-verified. **Phase P1 complete.**

## Phase P2 — Differentiators  ☑ (P2.3 full-screen render pending on-hardware)

- ☑ **P2.1 Device-class breadth.** Per-machine "sectors" for Optical (`PNPClass='CDROM'`),
  Bluetooth, WiFi via state-aware class enforcement; policy model + REST (`/sector`) + agent
  summary + WPF toggle buttons (Wi-Fi has a self-disconnect warning). Built; **verified**
  (block optical+wifi → agents API reflects it → agent received flags via `ApplyPolicy`).
  Device-level pnputil action needs on-hardware confirmation (same caveat as P0.1).
  - Files: `PolicyStore.cs`, `UsbPolicyManager.cs`, `AgentSignalRClient.cs` (policy object),
    `AgentController.cs`, `ServerClient.cs`, `MainWindow.xaml(.cs)`.
- ☑ **P2.2 BadUSB / keystroke-injection defense.** Agent watches for NEW keyboard
  enumerations (Rubber Ducky signature); baseline keyboards trusted; reports `KeyboardAdded`
  / `BadUSBBlocked` via new hub `ReportEvent`; optional targeted auto-block disables ONLY the
  new keyboard (never baseline — preserves the P0.1 HID rule); re-authorize via the allow-list.
  CEF sig 300 / severity 10. Built; **verified** (report→history→CEF; policy toggle propagates).
  - Files: `UsbPolicyManager.cs`, `Worker.cs`, `AgentSignalRClient.cs`, `AgentHub.cs`,
    `AgentController.cs`, `EventStore.cs`, `ServerClient.cs`, `MainWindow.xaml(.cs)`.
- ☑ **P2.3 Endpoint alert UX.** New **`USBGuardianNotifier`** WPF app runs in the user session
  (launched at logon) and shows a full-screen topmost SECURITY ALERT. Cross-session IPC:
  the agent writes an alert JSON to world-readable `C:\ProgramData\USBGuardian\alerts\` on each
  fresh block; the notifier `FileSystemWatcher` shows the alert (2s dwell before dismiss so an
  injected keystroke can't close it; 12s auto-close). Built (4 projects green); **IPC contract
  verified** (agent-written JSON parses on the notifier side). ⚠ The full-screen render itself
  needs a real logon session to confirm (not shown here to avoid hijacking the dev screen).
  Deferred sub-feature: hold-until-removed + per-group master-password desktop lock.
  - Files: `USBGuardianNotifier/*` (new project), `UsbPolicyManager.cs` (alert-write).
- ☑ **P2.4 Email alerts + CEF/SIEM logs.** CEF writer (reference-format `CEF:0|...`) + SMTP
  alerts on block events, both config-gated + fire-and-forget email. Built; **verified**
  (CEF line `|103|unauthorized device connection blocked|9|` written).
  - Files: `EventStore.cs`, `appsettings.json`.
- ☑ **P2.5 Remote client management.** reboot / shutdown / reload over SignalR `RemoteCommand`
  (20-sec user warning); REST + WPF buttons. Built; **verified** (reload reached the agent).
  Self-uninstall intentionally omitted (destructive; deferred).
  - Files: `AgentSignalRClient.cs`, `AgentController.cs`, `ServerClient.cs`, `MainWindow.xaml(.cs)`.
- ☑ **P2.6 Verify:** P2.1, P2.2, P2.4, P2.5 all integration-verified. Only P2.3 remains
  (deferred by design — needs a session-launched UI component + on-hardware test).

## Phase P3 — Enterprise / commercial (heavy; scope later)  ☐

- ☐ File-to-USB transfer monitoring
- ☐ AES-256 USB file encryption
- ◐ WiX Server/Endpoint/Control MSI authoring + GPO properties complete; Windows
  install/repair/major-upgrade/uninstall qualification remains
- ☐ Discovery (sysinfo / installed software / processes / NICs)
- ☐ License management + recovery; bulk client update
- ☐ Database backend + paging for 1000s of endpoints

---

## Execution order

P0 → P1 → P2, verifying + committing per phase. P3 is planned but scoped separately.
This document is updated (status marks) as phases complete.
