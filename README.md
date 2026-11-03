# ScreenSorter

A Windows tray app that sorts your screenshots automatically. It watches the folder where Win+Shift+S saves its files, works out where each screenshot came from, and moves it into a folder for that game or app:

```
Pictures/Screenshots/
├─ (new screenshots land here)
├─ Games/<Title>/
├─ Chat/<App>/
├─ Calls/
├─ Code/
├─ Streaming/
├─ Documents/
├─ Browser/
├─ Desktop/
├─ Other/
└─ _Review/        (anything it could not place with confidence)
```

Files are renamed to `<Name>_yyyy-MM-dd_HH-mm-ss.png`, for example `Games/Deadlock/Deadlock_2026-11-01_21-04-12.png`.

Everything runs on your own machine. Screenshots go only to a vision model running locally in LM Studio.

## How it decides

Each new screenshot goes through these stages:

1. **Black or blank image:** goes to `_Review`. Failed fullscreen captures often look like this.
2. **Focused window:** the app remembers which window had focus when the snip was taken. Rules in `appsettings.json` map apps to folders, and games are recognized by their install folder or exe name. This takes no model and is instant.
3. **Confirmed examples:** the screenshot is compared with examples learned from your corrections, using CLIP image embeddings stored in the app's own database.
4. **Vision model:** if the rules and examples can't place it, or they disagree, the file waits for the local vision model. The model runs only when the PC is idle: no input for 5 minutes, no fullscreen app, low GPU load. It is unloaded as soon as the work is done. A nearly identical shot taken within five minutes, with the same app in focus, copies the model's answer instead of asking again.

## Teaching it

Drag a screenshot into the right folder. A few seconds later it counts as an example of that folder and is renamed to match, for example `Screenshot 2026-11-01 210412.png` becomes `Deadlock_2026-11-01_21-04-12.png`. A name you chose yourself is kept. Moving a file into `_Review` removes the example. Renaming a folder relabels the files inside it.

## Install

Download `ScreenSorter-<version>-setup.exe` from [Releases](https://github.com/AVitrum/shot-sort/releases) and run it. It needs no administrator rights and no .NET install, and it goes to your own profile. The installer is not code-signed yet, so Windows SmartScreen may warn about it: choose **More info**, then **Run anyway**.

A tray icon appears; it may be hidden under the **^** arrow in the taskbar. A short guide opens the first time, and **nothing is moved until you close it**, so you can still change the settings first.

This app moves and renames files in your screenshots folder, **including screenshots that are already there**: those are sorted by image content. To keep them where they are, use **Open settings** on the guide's last step and untick the option.

Right-click the tray icon for Pause/Resume, Sort now (process waiting files without waiting for idle), the review and screenshots folders, History, Status, How it works, Settings and Exit. Double-click it for Settings.

**Status** shows whether LM Studio is running, how many examples were learned, and how many screenshots wait and why. If something stops while it is needed, a notification tells you once.

**History** lists where each screenshot went and why, with a preview. You can undo the app's move, which puts the file back and leaves it alone until you move it yourself, or send it to another folder, which teaches the app just like dragging it. **Accuracy** counts how often you moved what each stage placed, and which folders get mixed up most.

Uninstalling from Windows Settings removes the app and asks whether to delete `%LOCALAPPDATA%\ScreenSorter` too. Your screenshots stay either way. A silent uninstall keeps the data. If you delete it and install again, the app treats files already sorted as placed by you.

New versions install by themselves when the PC is idle, nothing waits to be sorted and no ScreenSorter window is open, and the app starts again afterward. To update by hand instead, untick **Install new versions automatically** under **Settings**. The installer is not code-signed, so SmartScreen still warns on a manual download. Automatic updates have their own protection: the app installs a download only if its SHA-256 matches a checksum file signed with a key that is kept off GitHub, so controlling a release is not enough to run code on your PC.

### Optional helper

| Needed for | What |
|---|---|
| Rules and learning from corrections | Nothing more; they work out of the box |
| Vision model | [LM Studio](https://lmstudio.ai/) with a vision model downloaded, and about 7 GB of free VRAM |

Without LM Studio, files no rule or example can place stay in the screenshots folder until it is available. To do without it, untick **Use a vision model** under **Settings > Vision model**: those files then go straight to `_Review`, and moving them from there teaches the app. A file already waiting goes to `_Review` the next time the app starts.

In LM Studio, download the model named under **Vision model** in Settings. When a screenshot needs the model and the PC is idle, ScreenSorter starts LM Studio's server with `lms server start` and leaves it running. To start it yourself instead, set `LmStudio:AutoStartServer` to `false` in `%LOCALAPPDATA%\ScreenSorter\settings.json`.

On first run the app downloads the CLIP image model (352 MB) from Hugging Face and checks it against a pinned SHA-256.

## Run from source

With the [.NET 10 SDK](https://dotnet.microsoft.com/download):

```bash
dotnet run --project src/ScreenSorter.App
```

To see how it behaves on a test folder first:

```bash
dotnet run --project src/ScreenSorter.App -- --Paths:WatchFolders:0=C:\Temp\shots --Paths:SortedRoot=C:\Temp\shots
```

## Existing screenshots

Screenshots taken while the app was not running have no record of the focused window, so only their image content counts. Files still waiting for the vision model when the app closes keep their record. Blank images go to `_Review`, files that closely match your examples are sorted right away, and the rest wait for the vision model behind any new screenshots. The model handles them while the PC is idle and stops as soon as you come back.

## Settings

Open **Settings** from the tray menu to change the watch folders, app rules, game aliases, vision categories, thresholds, idle conditions, retention and the LM Studio address, and to turn on **Start with Windows**. Saving restarts the app. Your values go to `%LOCALAPPDATA%\ScreenSorter\settings.json`, layered over the defaults in `src/ScreenSorter.App/appsettings.json`, so a rule you delete stays deleted.

The windows follow your Windows light or dark mode and accent color.

If a game's exe name differs from its title, add an alias on the **Games** page: `StarRail` and `Honkai Star Rail`.

Any setting can also be overridden for one run on the command line with `--Section:Key=value`.

## Your data

Data stays in `%LOCALAPPDATA%\ScreenSorter`: your settings, a marker that the guide was shown, the decision log and learned examples (SQLite), logs and the CLIP model. Examples hold image embeddings and folder names, not the images.

The decision log records file paths and the focused app's name, program path and window title. A window title can reveal a chat name, a document or a web page, so under **Settings > Privacy**:

- Window titles and program paths are erased after 30 days. Set 0 to not store them at all, or use **Erase them now**. They are overwritten on disk, not only marked as deleted.
- Log files are deleted after 14 days.

File paths and the rest of each record stay as long as the log does. They are how the app tells a file it sorted from one you placed yourself, so removing them would teach it from its own guesses. To remove everything, delete `%LOCALAPPDATA%\ScreenSorter`; the app then treats sorted files as placed by you.

The app reads window titles, process paths, and Steam and Epic install data. While classifying, it passes the window title to the local model as a hint. Its only network traffic outside this machine is the one-time model download and, unless you turn it off, a daily request to GitHub for the latest version and the download of a new one. Those requests carry no screenshots, file names or window titles.

## Current limits

- LM Studio itself is a separate install, and uninstalling ScreenSorter does not remove it.
- The vision model can pick the wrong game from your known games. Moving the file to the right folder teaches the app.
- Images only, no videos.

## Development

```bash
dotnet build ScreenSorter.slnx -v q -nologo
```

```bash
dotnet test ScreenSorter.slnx -v q -nologo
```

| Path | What it is |
|---|---|
| `src/ScreenSorter.Core` | Pipeline, rules, examples, models and interfaces |
| `src/ScreenSorter.Infrastructure` | Win32, file system, SQLite, LM Studio, CLIP |
| `src/ScreenSorter.App` | WPF tray host and `appsettings.json` |
| `tests/ScreenSorter.Tests` | xUnit tests; they use temp folders only |
| `tools/ScreenSorter.Probe` | Dev console for trying the model and rules without moving files |

Enable the safety hooks once per clone. They stop commits and pushes containing secrets, personal data, screenshots or very large files. CI runs the same rules on every push and pull request:

```bash
git config core.hooksPath .githooks
```

To build the installer locally (`artifacts/installer`):

```powershell
./installer/build-installer.ps1
```

A build for release needs `Updates:PublicKey` set in `appsettings.json`. Create the key once, on the machine you release from, and keep a copy of the key file and its passphrase somewhere safe. Without them, installed copies can never update by themselves again:

```powershell
dotnet run installer/release-key.cs -- new
```

Pushing a tag such as `v1.2.3` builds the installer on GitHub and attaches it to a draft release. Read the draft over, then sign and publish it from that machine:

```powershell
./installer/sign-release.ps1 -Tag v1.2.3
gh release edit v1.2.3 --draft=false
```

## License

[MIT](LICENSE). Third-party components are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
