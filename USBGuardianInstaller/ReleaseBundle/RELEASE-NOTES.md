# USB Guardian {{VERSION}} Acceptance Candidate Notes

Changes include per-machine endpoint enrollment, opt-in legacy shared-token migration, HTTPS enforcement, independent hardware/network workers, durable ownership before disable, accurate enforcement receipts, UASP/MTP queries, and dashboard selection guards.

The follow-up review fixes long-password BCrypt truncation with enhanced pre-hashing, rejects dashboard credential redirects, protects source-install token files before writing, enforces the Endpoint Windows capability floor, preserves multi-session notifier delivery, retries failed ownership cleanup without claiming completion, and stages updated enrollment instructions alongside the new packages.

Portable-device classification now excludes webcams/audio devices instead of treating camera-like names as storage. Audit filenames use identity hashes to prevent machine-name collisions; legacy records are filtered by identity, and retention replaces files atomically after durable appends. Runtime directories, file ownership and links are checked before trusting protected state.

Older password hashes remain compatible until password rotation. Rotate existing administrator passwords after upgrade, especially passwords exceeding 72 UTF-8 bytes. Do not deploy the historical 1.1.2/1.1.3 packages as if they contain these fixes.

The recurring idle console launch was traced to the separate AI-Control Watchdog scheduled task. Its launcher now uses pythonw instead of PowerShell. This is independent of USB Guardian packages.

A build, hash file, automated tests and valid signatures are separate from production acceptance. Real hardware, WPF/notifier interaction, LocalSystem service deployment, certificate trust, install/upgrade/repair/rollback/uninstall and power-loss recovery remain mandatory acceptance checks.

All projects currently target .NET 8. Plan a supported runtime migration before its support ends on 10 November 2026 and rebuild self-contained distributions with supported security patches.
