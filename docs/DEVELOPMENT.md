# Developing ResourceLens

ResourceLens is a .NET 10 Windows Forms tray app. Its dashboard is a local HTML/CSS/JS page (`ui/`) hosted in
WebView2, and its history is a SQLite database. The installer (`setup/`) is a small .NET Framework 4.8 app that
carries the main program compressed inside it.

## Build

Requirements: Windows 10/11 x64 and the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
build-setup.cmd
```

This runs `build-setup.ps1`, which:

1. publishes a trimmed, self-contained, single-file `publish\ResourceLens.exe` (about 47 MB; unused parts of .NET are removed);
2. gzips it into `dist\payload`;
3. builds `dist\ResourceLens-Setup.exe` (about 19 MB). .NET Framework 4.8 ships with Windows, so the installer needs nothing installed.

For day-to-day work, `dotnet build` produces a Debug build in `bin\Debug`.

## Test switches

| Switch | What it does |
|---|---|
| `--check=report.txt` | Full release check: calls every API method the window uses and serializes it the same way, writes both exports, opens every page in the real window, records JavaScript errors and takes pictures. Run it with `RESOURCELENS_DATADIR` pointing at a copy of the data. |
| `--check=… --shots --check-size=1440x900` | Same, plus a picture of every page at a fixed window size. The README screenshots are made this way. |
| `--selftest` | Compares the native readers (processes, memory, CPU) with .NET's own APIs. |
| `--bench` | Measures the cost of each sampling component. |
| `--probe-sensors` | Lists the sensor sources and GPU adapters found, with three readings, in `%TEMP%\resourcelens-sensors.txt`. |
| `--selftest-admin` | Tests per-process network (ETW) and the elevated logon task; needs administrator rights. |
| `--exit` | Asks the running copy to shut down cleanly (used by the installer). |
| `--uninstall [--quiet]` | What *Installed apps* runs. |
| `--devserver` (Debug only) | Serves the UI from `ui/` on disk at `http://localhost:5178` for fast UI work. |

Reports are written to `%TEMP%`.

`RESOURCELENS_DATADIR=<folder>` makes a run use its own data folder and its own single-instance scope. Such a test run
never touches the installed copy's autostart entry.

`RESOURCELENS_TEST_SENSORS` (Debug builds only) simulates other hardware: `basic` skips the vendor libraries (AMD ADL,
Lenovo WMI), which is what a typical Intel or NVIDIA PC has; `none` disables sensors entirely, as in a virtual machine.
Combine it with `--check` to see how every page behaves without those readings.

## Hardware notes

- Usage data comes from documented Windows interfaces (`NtQuerySystemInformation`, PDH counters with English names,
  ETW), so it does not depend on the hardware vendor or the Windows display language.
- GPUs are enumerated through D3DKMT, which gives each adapter's LUID; the PDH `GPU Engine` and `GPU Adapter Memory`
  instances carry the same LUID, which is how utilization, memory and temperature are matched per GPU. Compute-only
  adapters (NPUs), virtual displays and the Basic Render Driver are skipped.
- Temperatures: AMD ADL (Radeon GPUs and Ryzen APUs), D3DKMT adapter performance data (any GPU whose WDDM 2.4+ driver
  reports it), Lenovo GameZone WMI (administrator), and ACPI thermal zones as the last resort for the CPU. Accurate
  per-core CPU temperatures would need a kernel driver, which the project deliberately avoids.
- NVIDIA's NVML is not used: polling it keeps a laptop's discrete GPU awake, and D3DKMT already provides its
  temperature and clocks.
- The app is x64 only. On Windows 11 on ARM it runs under emulation (untested); the installer refuses Windows 10 on ARM.

> **Running from inside another app:** an app packaged with MSIX (for example the Claude desktop app, or a Store
> messaging app) redirects `%LOCALAPPDATA%` writes from programs it starts into its own private storage. ResourceLens
> detects this at startup (`src/Sandbox.cs`) and relaunches itself outside the container through WMI. Test runs with
> `RESOURCELENS_DATADIR` are left where they are.

## Layout

| File | Role |
|---|---|
| `src/Program.cs` | Entry point, single instance, command-line modes |
| `src/TrayApp.cs` | Tray icon, window, overlay, recorder watchdog, restarts |
| `src/Collector.cs` | Sampling: per app and per process, the active window, alerts |
| `src/ProcessScanner.cs` | All processes in one `NtQuerySystemInformation` call |
| `src/ProcessDetails.cs` | What each process is, from its command line (tab, GPU process, service, script…), with secrets hidden |
| `src/PdhCounters.cs` | Disk, network and GPU through PDH counters (independent of the Windows language) |
| `src/NetworkEtw.cs` | Per-process network through ETW (administrator) |
| `src/Sensors.cs`, `src/SensorHub.cs` | Temperatures, clocks and power (AMD ADL, D3DKMT, Lenovo WMI, ACPI), read on one shared thread |
| `src/Store.cs`, `src/StoreRepair.cs` | SQLite storage, rollups and retention; integrity checks and automatic repair |
| `src/Queries.cs`, `src/QueriesDetail.cs` | Time series, moments, per-process detail, insights |
| `src/Exporter.cs`, `src/SystemInfo.cs` | The AI-ready JSON report and CSV |
| `src/PerfMonitor.cs`, `src/HardwareInfo.cs` | The Performance page: 1-second samples, hardware details |
| `src/TaskManager.cs` | End task, priority, startup apps, services |
| `src/Features.cs` | Add-on modules: each one registers itself, receives every sample and keeps its data in `features.db` |
| `src/ModNet*.cs`, `ui/network.*` | Network monitor: live connections, usage, first-connection alerts, firewall blocking |
| `src/ModRules*.cs`, `ui/rules.*` | Process control: rules, ProBalance, power plans |
| `src/ModCounters*.cs`, `ui/counters.*` | Performance counters: browser, live chart, recording |
| `src/OverlayWindow.cs`, `src/HotkeyWindow.cs` | The gaming overlay and its hotkey |
| `src/MainForm.cs`, `src/Api.cs` | The WebView2 window and its bridge to the page |
| `src/Autostart.cs`, `src/Uninstaller.cs`, `src/Sandbox.cs` | Autostart (Run key or elevated logon task), uninstall, container detection |
| `src/AppCheck.cs` | The `--check` release check |
| `setup/` | The installer |
| `ui/` | The dashboard: HTML/CSS/JS, ECharts and the IBM Plex Sans Arabic font, all local |

## Data

`%LOCALAPPDATA%\ResourceLens`:

| File | Contents |
|---|---|
| `history.db` | The recorder's database. Tables: `sys` (one row per snapshot), `proc` (per app), `inst` and `iproc` (per process), `names`, `strings`, `events`. |
| `features.db` | The add-on modules' data. |
| `settings.json` | Settings. |
| `errors.log` | Errors, if any. |
