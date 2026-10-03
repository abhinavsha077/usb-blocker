# Control GUI redesign review

Reviewed on 2026-10-03 using the archive-code-review workflow.

## Summary

The Control UI now uses a permanent navigation rail, a full-height endpoint
browser, a fleet summary strip and dedicated Devices / Policies / Manage pages.
The WPF theme includes custom fields, dropdowns, checkboxes, expanders and
scrollbars, alongside consistent dialogs and separate saved-policy and observed
state columns. The published Control payload is available in `Dist/Control`.

## Findings

- Blocking: none remain in the reviewed GUI change.
- High: none remain. Simulated HTTP failures disable commands; filtering out a
  selection clears its command target; late device replies do not change the
  selected endpoint; offline power actions stay disabled.
- Medium: live hardware, high-DPI scaling and assistive-technology operation
  remain manual acceptance work. The checks render WPF content without opening
  visible windows, so they do not certify every Windows display configuration.
- Low: no open findings requiring follow-up in this change.

## Validation

- Solution build succeeded.
- Existing test suite: 100 passed, 0 failed.
- `tools/ControlGuiChecks`: 52 checks passed. Uses a deterministic in-memory HTTP
  handler; no real policy commands or credential writes.
- Navigation buttons switch actual workspaces; device inventory is selected at
  startup; field viewports leave enough room for text; policy controls below the
  fold remain reachable through scrolling.
- Visually inspected generated dashboard, compact viewport, disconnected state,
  access policy, administration, event history and password dialog previews.
- Control Release publish succeeded. `Start-Control.bat` resolves this payload.
- Backend enforcement, authentication and Windows service accounts were not
  changed by the GUI redesign.

## Verdict

Approve the GUI change. Live USB acceptance remains required for deployment
claims about hardware enforcement, as described in the repository README.
