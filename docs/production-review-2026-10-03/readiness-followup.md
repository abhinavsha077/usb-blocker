# USB Guardian readiness review and release plan

Reviewed 3 October 2026. **Production verdict: DO NOT SHIP yet.** The confirmed source defects found during this review have fixes and regression evidence. The current source and three version 1.1.4 installer candidates build successfully. Hardware, interactive desktop, service deployment, certificate trust and installer lifecycle acceptance remain unverified; the candidates are unsigned.

This report supersedes the readiness conclusions in the historical [initial review](qa-report.md), without rewriting its before-fix evidence. It reviews the current working tree, including existing changes, rather than certifying an immutable commit. No real USB hardware was disabled/enabled, no Guardian services were installed, and no MSI was installed on this workstation during these checks.

Normal enforcement covers removable USB storage (USBSTOR/UASP) and USB portable devices (WPD/MTP). It deliberately protects hubs, controllers and ordinary input devices. It does **not** establish a claim that every USB class can be blocked/unblocked, prevent all first-arrival access, or provide complete BadUSB protection. Optional sector/BadUSB controls need separate approved hardware tests.

## Verification

| Check | Result | Evidence and limits |
|---|---|---|
| Entire solution Release build | PASS | .NET 8, zero warnings/errors |
| Final regression suite | PASS | 109 passed, zero failed/skipped; [TRX](../../USBGuardianTests/TestResults/readiness-followup.trx) |
| Isolated integration probes | PASS | 34/34; [results](evidence/integration-fixed-results.json), [probe](Probe/Program.cs) |
| Production WMI queries | PASS, read-only | Actual queries execute; corrected portable query excludes the host's webcam. Host had zero storage/portable matches, so this is not a hardware block test |
| PowerShell provisioning/config writer | PASS | Windows PowerShell 5.1 and PowerShell 7; protected writes, failed-replacement preservation, no token printing; [PS7 evidence](evidence/protected-config-ps7.json) |
| NuGet vulnerability query | PASS | No known vulnerable dependencies reported for five projects, including transitives, on review date; a feed query does not certify all code |
| Fresh self-contained x64 packaging | PASS | Server, Endpoint and Control 1.1.4; [successful build](evidence/package-followup.log), [metadata/hashes](evidence/package-followup-results.json) |
| Production signing gate | PASS as a rejection test | `-Production` without a certificate rejects before publishing; valid trusted signing still unverified |
| Artifact signatures | NOT READY | All three candidate MSIs are `NotSigned` |
| Native WPF visual flows | NOT VERIFIED | No native automation/visual acceptance available; no screenshots or UI PASS claimed |
| LocalSystem physical USB behavior | NOT VERIFIED | Simulated hardware tests and read-only WMI do not replace device acceptance |
| Install/upgrade/repair/rollback/uninstall | NOT VERIFIED | WiX authoring and package generation tested; no lifecycle installation performed |
| Real HTTPS rollout | NOT VERIFIED | HTTP rejection tested centrally for API/hub; actual certificate/private-key/endpoint trust remains a deployment gate |
| Recurring terminal window | RESOLVED, user-confirmed | Separate watchdog launcher fixed; user confirmed no idle recurrence |

Integration tests use a temporary loopback Server, generated credentials, simulated SignalR endpoints and isolated state. Failure injection locks temporary files. Production state, services and devices are not exercised. Historical `integration-results.json`, `password-before-fix.trx`, earlier package versions and failed package-attempt logs are reproduction evidence, not the final result.

## Findings addressed

Line references below point to the fixed source. P1 denotes a serious correctness/security defect; P2 denotes a user-facing reliability defect.

| ID / priority | Confirmed failure | Fix and evidence |
|---|---|---|
| F01 / P1 | Portable-device name heuristics matched the host's built-in HP TrueVision webcam as removable storage | [UsbDeviceQueries.cs](../../USBGuardianAgent/UsbDeviceQueries.cs:7) uses WPD/PortableDevice class plus USB identity; [UsbPolicyManager.cs](../../USBGuardianAgent/UsbPolicyManager.cs:696) rejects camera/audio functions in enforcement and inventory. Live WMI and simulated non-storage/phone/UASP tests pass |
| F02 / P1 | Passwords sharing the first 72 UTF-8 bytes authenticated despite a different suffix | [ServerConfigManager.cs](../../USBGuardianServer/ServerConfigManager.cs:120) uses enhanced BCrypt pre-hashing for new/rotated passwords, persists format marker and restores old state on save failure. ASCII/Unicode/restart/migration tests pass; [before-fix failure](../../USBGuardianTests/TestResults/password-before-fix.trx). Existing passwords require rotation to migrate |
| F03 / P1 | Dashboard HTTP redirects could forward the custom administrator-password header to another origin | [ServerClient.cs](../../USBGuardianControl/ServerClient.cs:181) disables automatic redirects and validates an HTTP(S) origin. Real loopback 302 regression verifies no credential reaches the other listener |
| F04 / P1 | Source installation could write endpoint secrets before applying protected permissions | [Write-AgentConfig.ps1](../../Write-AgentConfig.ps1:1) creates protected temporary files before writing, flushes and atomically replaces; Install-Agent uses it. Elevated writers assign Administrators ownership; failed replacement preserves existing configuration. Actual subprocess ACL tests pass |
| F05 / P1 | File permissions alone left runtime parent replacement, link and untrusted-owner hazards | [WindowsFileSecurity.cs](../../Shared/WindowsFileSecurity.cs:9) protects known runtime parents, rejects links and refuses non-SYSTEM/non-Administrators file owners under LocalSystem. Writes/appends flush to disk. Temp-directory ACL tests pass; adversarial LocalSystem/race/power-loss qualification remains pending |
| F06 / P2 | One notifier session deleted shared alerts before another session read them; busy/startup files could be missed and polling blocked UI | [BlockAlertInbox.cs](../../USBGuardianNotifier/BlockAlertInbox.cs:1) has per-session bounded processing, retry and validation. [App.xaml.cs](../../USBGuardianNotifier/App.xaml.cs:90) scans in the background, installs watcher before initial scan and retains fallback polling. Two-inbox/busy/oversize/timestamp tests pass; actual interactive sessions still need acceptance |
| F07 / P1 | Failed ownership-journal cleanup could still report successful enforcement | [UsbPolicyManager.cs](../../USBGuardianAgent/UsbPolicyManager.cs:956) marks failed journal writes as unverified and retries cleanup without re-toggling correctly enabled hardware. Failure-injection regression passes |
| F08 / P1 | Endpoint installation allowed Windows builds lacking required pnputil enable/disable commands | [Endpoint Product.wxs](../../USBGuardianInstaller/Endpoint/Product.wxs:20), source installer and runtime guard require build 19041+. Authoring tests pass; this capability floor does not certify an unsupported Windows edition |
| F09 / P1 | Different machine IDs sanitizing to the same filename shared audit history/receipt state | [EventStore.cs](../../USBGuardianServer/EventStore.cs:65) hashes case-normalized full identities for new paths, filters legacy records by exact identity and refuses unreadable receipt initialization. Collision/migration/restart tests pass |
| F10 / P1 | In-place retention could damage an event log after an event had been acknowledged | [EventStore.cs](../../USBGuardianServer/EventStore.cs:277) uses bounded streaming retention and protected atomic replacement, retaining the durable append if compaction fails. Latest-acknowledgement and locked-append tests pass; power-loss injection remains pending |
| F11 / P1 | Bundle instructions reused shared-token assumptions, and retained WiX intermediates skipped linking after a version change | [Build-Installers.ps1](../../USBGuardianInstaller/Build-Installers.ps1:1) uses fresh scoped intermediates and stages current per-endpoint enrollment helpers/docs with the exact packages. Deployment helpers do not equate installation with verified enforcement. Corrected full packaging succeeds |
| F12 / P1 release control | An unsigned build could be mistaken for a production release | `-Production` requires a signing certificate and verifies payload/MSI signatures. Bundle clearly labels unsigned candidates; actual trusted signing is still a release blocker |

The earlier blockers were also fixed and remain covered: WQL escaping/UASP matching, input/shared-parent safety, independent enforcement/network workers, bounded network operations, learn-mode clearing/expiry/offline behavior, durable policy cache and ownership before action, transactional policy/event failure handling, enrolled per-machine credentials, central HTTPS requirements, stale/failed enforcement receipts, dashboard selection races and MSI MachineId/configuration precedence. The final 109 tests and 34 probes include these paths.

## Terminal investigation

The checks-only flashes were addressed with the required quiet runner (`tty:true`, `login:false`) and hidden runtime subprocess settings. The later idle recurrence was traced to the **AI-Control Watchdog** scheduled task in the separate `T:\ArcX\Arc - Ai Open Route` project, launching PowerShell periodically. No running USB Guardian process caused that recurrence.

Its action and registration script now use the virtual environment's `pythonw.exe -m ai_control.cli watchdog`, which does not allocate a console. A direct windowless invocation exited successfully, and the user confirmed the idle window stopped. The task was found disabled during investigation; that state was preserved. It was not enabled to create additional background activity. The [saved task definition](evidence/ai-control-watchdog-before.xml) records the old launcher. This external launcher change is not part of the Guardian MSI payloads.

## Security boundaries and residual risk

| Boundary / assets | STRIDE checks and verified controls | Remaining qualification |
|---|---|---|
| Administrator client to privileged API | Spoofing/disclosure: password rotation, enhanced hashes, denied anonymous/default remote admin, rate limits, redirect refusal, origin validation, HTTPS requirement | Deploy trusted TLS and rotate existing legacy hashes |
| Endpoint to Server hub | Spoofing/tampering: unique enrollment, hashed credentials, identity binding, rotation/revocation, secure transport gate; failed persistence cannot falsely ACK | Real fleet enrollment/reconnection and certificate trust |
| Local files to LocalSystem service | Tampering/elevation: protected parents/files, trusted owner checks, rejected links, atomic writes, ownership before disable, corrupt-state preservation | Adversarial LocalSystem startup, races, disk-full/crash/power-loss tests |
| Security events and receipts | Repudiation: durable queue, failed append refusal, identity-safe history/receipts and atomic retention | Verify retention/export operational requirements and sustained load |
| Network/subprocess/inbox input | Denial of service: bounded network waits, independent hardware worker, hidden structured subprocesses, retries, bounded notifier parsing | Fleet sizing, UI responsiveness and long-running hardware pilot |

Overall release risk remains **high until the gates below pass**. The source changes improve code health; production-release approval remains **request changes / do not ship**. No claim of a complete security certification is made by passing these checks.

## Concrete release plan

| Order | Required work | Exit evidence |
|---|---|---|
| 1 | Qualify normal USB blocking/unblocking on approved LocalSystem endpoints with USBSTOR, UASP, WPD/MTP, composite docks, keyboards/mice/webcam/audio; test allow/block/lock/unlock, arrival/removal, expiry, offline, reboot, idempotence and externally disabled devices | Completed [acceptance checklist](../../USBGuardianInstaller/output/Bundle/TEST-CHECKLIST.md), exact IDs/exit results, measured first-arrival/block latency, fresh verified dashboard receipts |
| 2 | Test WPF Control and Notifier interactively: connect/authentication/reconnect, selection changes, pending/stale/error display, alerts in two simultaneous sessions, logon/startup, dismissal dwell and auto-close | Visual evidence and no missed/incorrect alerts; no console or reconnect loops |
| 3 | Clean-VM install, upgrade from supported older deployments, repair, failed upgrade rollback, uninstall and reinstall; include locked/corrupt/disk-full/crash/absent-device recovery | Configuration preserved, services remain LocalSystem, credentials/ACLs correct, failed recovery IDs retained and retry succeeds with retained Agent executable |
| 4 | Deploy trusted HTTPS, private-key permissions, endpoint certificate trust/firewall; enroll distinct machine tokens; disable legacy/development authentication; rotate existing admin hashes | HTTPS success and HTTP/invalid-token/untrusted-certificate rejection from actual endpoint machines |
| 5 | Build using `Build-Installers.ps1 -Version 1.1.4 -Production -CertificateThumbprint <trusted certificate>` and complete package integrity checks | Trusted Authenticode validation for four EXEs and three MSIs, fresh hashes, lifecycle checks against those exact signed artifacts |
| 6 | Run a limited fleet pilot, establish support/recovery/log-retention procedures and runtime patch/migration ownership before general rollout | Recorded acceptance results, rollback/recovery rehearsal, supported runtime plan and pilot sign-off |

Endpoint uninstall recovery is best-effort: [WiX custom action](../../USBGuardianInstaller/Endpoint/Product.wxs:82) uses `Return="ignore"`. A successful uninstall therefore does not prove every managed device was restored. Preserve the ownership journal and an Agent executable for an elevated `--restore-managed-devices` retry; qualify absent-device and failed-enable cases before rollout.

The Windows build floor follows Microsoft's [PnPUtil command documentation](https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/pnputil-command-syntax). All projects retain the required .NET 8 target. Its support ends **10 November 2026**, so general deployment needs a runtime migration/maintenance plan before that date, per Microsoft's [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core).

## Candidate deliverables and reproduction

Current candidate entry point: [Bundle/START-HERE.md](../../USBGuardianInstaller/output/Bundle/START-HERE.md). The bundle contains all three 1.1.4 MSIs, SHA-256 manifest, enrollment/install helpers, secure example and acceptance instructions. Older 1.1.2/1.1.3 packages do not contain the complete follow-up fixes. Scripts/docs were synchronized after packaging; no compiled runtime source changed after the successful final MSI build.

```powershell
dotnet build "USB Blocker.sln" -c Release --no-restore
dotnet test USBGuardianTests/USBGuardianTests.csproj -c Release --no-build
dotnet run --project docs/production-review-2026-10-03/Probe/Probe.csproj -c Release --no-restore -- 'T:\ArcX\USB Blocker' (Get-Process -Id $PID).Path
```

On this Windows host, Codex shell checks use `tty:true` and `login:false`. These commands do not qualify physical hardware or authorize deploying the unsigned candidates. Release approval requires the recorded gates above, not just a successful build.

