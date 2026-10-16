# Third-party notices

ResourceLens itself is released under the [MIT License](LICENSE). The released program also contains the
following components, each under its own license:

| Component | Used for | License | Notice / text |
|---|---|---|---|
| [Apache ECharts](https://echarts.apache.org/) 5.6.0 | Charts in the dashboard | Apache-2.0 | [ui/vendor/echarts.LICENSE.txt](ui/vendor/echarts.LICENSE.txt) |
| [IBM Plex Sans Arabic](https://github.com/IBM/plex) | Interface font | SIL Open Font License 1.1 | [ui/fonts/OFL-LICENSE.txt](ui/fonts/OFL-LICENSE.txt) |
| [Microsoft Edge WebView2 SDK](https://aka.ms/webview) 1.0.3485.44 | Hosting the dashboard window | BSD-style (Microsoft) | [licenses/WebView2-LICENSE.txt](licenses/WebView2-LICENSE.txt), [licenses/WebView2-NOTICE.txt](licenses/WebView2-NOTICE.txt) |
| [SQLite](https://sqlite.org/) 3.53 | The history database | Public domain | https://sqlite.org/copyright.html |
| [SQLitePCLRaw](https://github.com/ericsink/SQLitePCL.raw) 3.0.5 | SQLite bindings for .NET | Apache-2.0 | https://www.apache.org/licenses/LICENSE-2.0 |
| [Microsoft.Data.Sqlite](https://github.com/dotnet/efcore) 10.0 | SQLite access | MIT | https://github.com/dotnet/efcore/blob/main/LICENSE.txt |
| [System.Management](https://github.com/dotnet/runtime) 10.0 | WMI (Lenovo sensors, relaunch) | MIT | https://github.com/dotnet/runtime/blob/main/LICENSE.TXT |
| [.NET runtime and Windows Forms](https://dotnet.microsoft.com/) 10 | Self-contained runtime inside the exe | MIT | https://github.com/dotnet/runtime/blob/main/LICENSE.TXT |

The Microsoft Edge WebView2 **Runtime** is not included; it ships with Windows 11 and recent Windows 10.

ResourceLens reads hardware sensors through the AMD Display Library (`atiadlxx.dll`) and Lenovo's WMI provider
when they are present on the computer. Neither is included or redistributed.
