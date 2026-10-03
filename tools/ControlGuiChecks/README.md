# Control GUI checks

Run on Windows with .NET 8 from the solution root:

```powershell
dotnet run --project tools/ControlGuiChecks/ControlGuiChecks.csproj
```

The runner loads the actual compiled WPF views on an STA dispatcher. It uses an
in-memory HTTP handler and does not open application windows, contact a real
server, save credentials, or send commands to endpoints.

It checks endpoint search and filtering, selection clearing, policy commands,
duplicate-command guards, infrastructure visibility, offline restrictions,
connection failure/recovery, navigation, input text clipping, policy scrolling,
history errors and password validation. It also
renders the dashboard, tabs and dialogs at normal and compact viewport sizes.

PNG previews with simulated inventory are written to `artifacts/gui/` (ignored
by Git). Pass a directory argument after `--` to choose another output location.
These checks do not replace live hardware acceptance testing in `README.md`.
