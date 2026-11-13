# Security

## Reporting a vulnerability

Please report security problems privately through GitHub: on this repository's **Security** tab, choose **Report a vulnerability**. Do not open a public issue for them.

Include what you found, how to reproduce it, and which version you used (the Status window shows it, as does the installer's file name). A fix ships as a new release, which installed copies pick up by themselves.

## Supported versions

Only the latest release gets fixes. Installed copies update themselves unless automatic updates are turned off in Settings.

## What the app protects

ScreenSorter runs on your own PC and handles things that can be private:

- **Screenshots**, which can show chats, documents, passwords or anything else that was on screen.
- **Window titles and program paths** of the focused app, which can name a chat, a document or a web page.

Both stay on your PC. The app keeps no account and sends nothing to its author.

## Where outside input enters, and what the app does about it

| Input | Risk | What the app does |
|---|---|---|
| The vision model's answer | Text in a screenshot can steer the model, and its answer becomes a folder name. | Folder names are cleaned (no path separators, `..`, device names like `CON`, invisible formatting characters, trailing dots or spaces) and the final path must lie inside the sorted folder, or the file goes to `Unknown`. |
| The LM Studio address | Screenshots are sent there. | Only this PC (`localhost`, `127.x`, `::1`) is used, unless you turn on **Allow another computer**; another computer must use https. Settings, the model client and the Status page all enforce this. |
| Updates | A tampered installer would run with your account's rights. | Releases are checked against a signature made with a key kept off GitHub (ECDSA P-256 over the installer's SHA-256 checksum), the checksum must name that exact installer, and downloads must use https. An unsigned or mismatched release is not installed. |
| The CLIP model download | A swapped model file. | The download must match a fixed SHA-256 hash, or it is discarded. |
| Files in the watched folder | Any program can put a file there. | Images are decoded by Windows' own imaging component; a file that cannot be read goes to `_Review`. |
| Steam and Epic library files | Read to learn game names. | Read only; nothing in them is run. |

## What the app stores

In `%LOCALAPPDATA%\ScreenSorter`: your settings, a SQLite decision log (file paths, the focused app's name, and its window title and program path for 30 days by default), learned examples (image embeddings and folder names, not images), logs (14 days by default) and the CLIP model. The [README](README.md#your-data) explains how to shorten or erase these.

## What is out of scope

- Anything that already runs as your Windows user can read and change the app's files, settings and screenshots. The app does not try to protect against your own account.
- LM Studio itself, and the vision model you load into it.
- The installer is not Authenticode-signed, so Windows SmartScreen may warn before the first install. Updates after that are verified as described above.
