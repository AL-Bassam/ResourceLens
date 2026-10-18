# Changelog

## 1.3.0

Hardware compatibility.

- The Performance page lists every GPU separately (GPU 0, GPU 1…), each with its own utilization, engines, memory, temperature and clocks. Previously only the first adapter was shown, which on laptops with two GPUs could be the integrated one.
- GPU utilization is calculated per engine and per adapter, as in Task Manager. Previously, engines of the same type were added together across GPUs, which overstated usage on laptops with integrated and discrete graphics.
- On AMD Ryzen laptops with a discrete GPU (for example NVIDIA GeForce), the recorded GPU temperature is now the discrete GPU's instead of the integrated GPU's.
- NPU activity is not counted as GPU utilization, and virtual display adapters (remote desktop, game streaming) are not listed as GPUs.
- The GPU list shows only adapters that are present. Previously, cards removed from the PC could still appear.
- An ACPI thermal zone that reports a fixed value is ignored after the first five minutes. Some desktop boards report a constant temperature, which was recorded as the CPU temperature.
- The note about fan speeds and administrator rights is shown only on Lenovo devices.
- When the WebView2 Runtime is missing, the message is shown in the interface language and offers to open the download page.
- The installer stops with an explanation on Windows 10 on ARM, which cannot run x64 applications.
- Without a saved setting, the interface language follows the Windows display language.

## 1.2.0

First public release.

- Recording since boot of CPU, memory, disk, network, GPU, temperatures and clock speeds, per application and per process.
- Snapshot list, interactive charts with zoom and time ranges, insights, and export to JSON and CSV.
- Task Manager functions: the Performance page, end task, priority, startup applications and services.
- Network monitor: active connections, daily and monthly usage, first-connection alerts and firewall blocking.
- Process rules, automatic priority balancing and power plans.
- Performance counters.
- In-game overlay (`Ctrl+Shift+O`).
- About 25% less disk space per snapshot than 1.1.
- The history database is checked and repaired automatically, and the recorder restarts itself if it stops responding.
- The application and its installer relaunch themselves outside an app container (MSIX), so that their files and registry entries are written to the real locations.
- Installer: per-user, no administrator rights, Arabic and English, in-place updates, uninstall from *Installed apps*.
