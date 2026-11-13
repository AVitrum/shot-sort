# End-to-end scripts

These scripts run ScreenSorter as a real process and drive its windows, tray menu, installer and updater. They cover
what the in-process tests in `tests/ScreenSorter.Tests/EndToEnd` cannot: WPF windows, the tray icon, the first-run
hold, the self-contained build, the installer and a real update.

Each run uses its own folders under `%TEMP%\ScreenSorterE2E` (or `SCREENSORTER_E2E_ROOT`). The app is started with
`--App:InstanceName=e2e` and every path pointing there, so it runs beside an installed ScreenSorter and never touches
the real Screenshots folder, data or settings. LM Studio and the CLIP download point at an unreachable address.

Windows PowerShell 5.1 and a desktop session are needed. Do not use the mouse or keyboard while they run.

```powershell
dotnet build ScreenSorter.slnx
powershell -NoProfile -ExecutionPolicy Bypass -File tests/e2e/run-all.ps1          # ~3 min
powershell -NoProfile -ExecutionPolicy Bypass -File tests/e2e/run-all.ps1 -Slow    # + CLIP store and self-contained run
```

Each script prints `ok` or `FAIL` per check and exits with the number of failures.

| Script | What it checks | Needs |
|---|---|---|
| `hold.ps1` | First-run guide holds sorting until it closes; settings from the guide | build |
| `history.ps1` | History page, undo, the undone file is left alone across a restart; Accuracy page | build |
| `privacy.ps1` | Old logs deleted, window titles erased, privacy settings validated and saved | build |
| `status.ps1 [-Live]` | Status page with LM Studio down and a waiting file; `-Live` reads a running LM Studio | build |
| `tray.ps1` | Every tray menu item, double-click, pause, Sort now, Exit | build |
| `store.ps1 [-ModelPath]` | CLIP examples: learn, relabel, rebuild from the log, sort by example, forget | build, CLIP model |
| `installed-run.ps1` | Self-contained publish starts and sorts | SDK |
| `install.ps1 -Setup <exe>` | Silent install, the installed app sorts, silent uninstall leaves nothing | `build-installer.ps1 -SmokeTest` |
| `uninstall-ask.ps1 -Setup <exe>` | The uninstaller's question about app data, both answers and silent mode | `build-installer.ps1 -SmokeTest -SmokeMutex` |
| `update.ps1 -OldSetup <exe> -NewSetup <exe>` | A 0.0.1 install finds, verifies (throwaway signing key) and installs 0.0.2, which then runs | two smoke installers, 0.0.1 and 0.0.2, ScreenSorter not running |

Window pictures for a person to look over are saved under `%TEMP%\ScreenSorterE2E\ui`; no script checks them.
