# Changelog

## 1.2.0

First public release.

- History since boot of CPU, memory, disk, network, GPU, temperatures and clocks, per app and per process.
- Moments, interactive charts with zoom and time spans, insights, and export to JSON (AI-ready) and CSV.
- A Task Manager replacement: the Performance page, end task and priority, startup apps, services.
- New pages:
  - Network monitor: live connections, daily and monthly usage, first-connection alerts, firewall blocking.
  - Process control: per-app rules, ProBalance, power plans.
  - Performance counters.
- Gaming overlay (`Ctrl+Shift+O`).
- About 25% less disk space per snapshot than 1.1.
- The history database checks and repairs itself; the recorder restarts itself if it gets stuck.
- The app and its installer relaunch themselves outside an app container (MSIX) so their files and registry entries are real.
- Installer: per-user, no administrator rights, Arabic and English, update in place, uninstall from *Installed apps*.
