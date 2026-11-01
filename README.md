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
3. **Confirmed examples:** the screenshot is compared with examples learned from your corrections, using CLIP image embeddings stored in Qdrant.
4. **Vision model:** if the rules and examples can't place it, or they disagree, the file waits for the local vision model. The model runs only when the PC is idle: no input for 5 minutes, no fullscreen app, low GPU load. It is unloaded as soon as the work is done.

## Teaching it

Drag a screenshot into the right folder. A few seconds later it counts as an example of that folder and is renamed to match, for example `Screenshot 2026-11-01 210412.png` becomes `Deadlock_2026-11-01_21-04-12.png`. A name you chose yourself is kept. Moving a file into `_Review` removes the example. Renaming a folder relabels the files inside it.

## Requirements

| Needed for | What |
|---|---|
| Everything | Windows, [.NET 10 SDK](https://dotnet.microsoft.com/download) |
| Learning from corrections | [Docker Desktop](https://www.docker.com/products/docker-desktop/), for Qdrant |
| Vision model | [LM Studio](https://lmstudio.ai/) with its server on, a vision model downloaded, and about 7 GB of free VRAM |

Rules work without Docker or LM Studio. Without Qdrant the example stage is skipped. Without LM Studio, files no rule can place stay in the screenshots folder until it is available.

## Getting started

Start Qdrant:

```bash
docker compose up -d
```

In LM Studio, download the model named by `LmStudio:Model` in `src/ScreenSorter.App/appsettings.json` and start the server. Then run the app:

```bash
dotnet run --project src/ScreenSorter.App
```

A tray icon appears; it may be hidden under the **^** arrow in the taskbar. Right-click it for Pause/Resume, Sort now (process waiting files without waiting for idle), the review and screenshots folders, Settings and Exit.

On first run the app downloads the CLIP image model (352 MB) from Hugging Face and checks it against a pinned SHA-256.

> **Note:** this app moves and renames files in your screenshots folder, **including screenshots that are already there**: those are sorted by image content when you start it. To see how it behaves first, create an empty test folder and point it there:
> `dotnet run --project src/ScreenSorter.App -- --Paths:WatchFolders:0=C:\Temp\shots --Paths:SortedRoot=C:\Temp\shots`
>
> To keep existing screenshots where they are, add `--Backlog:Enabled=false`, or untick the option under Settings.

## Existing screenshots

Screenshots taken while the app was not running have no record of the focused window, so only their image content counts. Blank images go to `_Review`, files that closely match your examples are sorted right away, and the rest wait for the vision model behind any new screenshots. The model handles them while the PC is idle and stops as soon as you come back.

## Settings

Open **Settings** from the tray menu to change the watch folders, app rules, game aliases, vision categories, thresholds, idle conditions and the LM Studio address, and to turn on **Start with Windows**. Saving restarts the app. Your values go to `%LOCALAPPDATA%\ScreenSorter\settings.json`, layered over the defaults in `src/ScreenSorter.App/appsettings.json`, so a rule you delete stays deleted.

If a game's exe name differs from its title, add an alias on the **Games** tab: `StarRail` and `Honkai Star Rail`.

Any setting can also be overridden for one run on the command line with `--Section:Key=value`.

## Your data

Data stays in `%LOCALAPPDATA%\ScreenSorter`: your settings, the decision log (SQLite), logs and the CLIP model. The decision log records file paths and the focused app's name and window title. Qdrant's data stays in a local Docker volume, reachable only from this machine.

The app reads window titles, process paths, and Steam and Epic install data. Its only network traffic outside this machine is the one-time model download.

## Current limits

- The app does not start LM Studio's server or Qdrant. After a reboot, files that need them wait until they are running.
- There is no undo. Every move is recorded in the decision log, and you can drag a file back by hand.
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
| `src/ScreenSorter.Infrastructure` | Win32, file system, SQLite, LM Studio, Qdrant, CLIP |
| `src/ScreenSorter.App` | WPF tray host and `appsettings.json` |
| `tests/ScreenSorter.Tests` | xUnit tests; they use temp folders only |
| `tools/ScreenSorter.Probe` | Dev console for trying the model and rules without moving files |

Enable the safety hooks once per clone. They stop commits and pushes containing secrets, personal data, screenshots or very large files:

```bash
git config core.hooksPath .githooks
```
