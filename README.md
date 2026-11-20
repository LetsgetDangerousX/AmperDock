# AmperDock

A cute-but-clean dock for Windows 10/11, by Ampersite LLC.

**Download:** get `Ampersite.AmperDock-win-Setup.exe` from [Releases](https://github.com/LetsgetDangerousX/AmperDock/releases). It installs
for you only (no admin) and updates itself.

## Run it from source

Needs the .NET 10 SDK (`winget install Microsoft.DotNet.SDK.10`).

```
dotnet run --project src/AmperDock.App
```

- Right-click the dock to add apps (**Add an app…** lists everything in Start, Store apps included), files or folders, change its position or appearance, set **Auto-hide** (and how long to rest at the screen edge before it comes back) or **Window previews**, or **Quit AmperDock**. You can also drag apps from the Start menu, and apps, files and folders from Explorer, onto the dock.
- Rest the pointer on an open app to see live previews of its windows: click one to switch to it, or use its ✕ to close it.
- Right-click a dock item → **Edit** → **Choose an AmperDock icon…** for the 3D icons, Planet Pals and folder icons.
- Dock appearance has 15 dock themes, **Dock colours follow my wallpaper**, **Icon colours follow my wallpaper** (separately) and **Reflections**.
- Right-click → **Add a desktop widget**: a note, a clock, a countdown, the weather or Mochi the cat. They sit on the desktop; drag them anywhere, right-click for colours (notes also have text colour, font and size), and drag a corner to resize.
- Look packs → **Theme packs** sets a wallpaper, dock theme, cursors, trail and folder icons together (or the ones you tick), with Undo.
- Move the dock by dragging an empty part of it (or Alt+drag anywhere on it): drop it near any screen edge to dock it there, or anywhere else to float.
- Settings live in `%APPDATA%\AmperDock\amperdock.db`. Use `--db <path>` to try it with a throwaway database.
- `--gallery` opens the developer component gallery (a test page for the UI building blocks, not settings).
- `--selftest-tasks report.txt` writes what the dock sees: open windows with their program and app id, each item's match, and the app list.
- `--selftest-looks report.txt` applies and restores a cursor set and a folder icon on a temp folder, and checks everything came back exactly.
- Set `AMPERDOCK_TRACE=1` to write a trace log to `%LOCALAPPDATA%\AmperDock\logs\`.

## Look packs (cursors, trails, folder icons)

Right-click the dock → **Look packs**. Cursors and folder icons change real Windows settings, and both can be
undone from the same window ("Your original cursors", and Restore next to each folder).

All art (icons, cursors, trails, dock themes, wallpapers) is original, © Ampersite LLC. The finished files the app uses are in
`src/AmperDock.App/LookPacks` and `src/AmperDock.App/DockThemes`.

## Installer

```
powershell -ExecutionPolicy Bypass -File tools\make_installer.ps1 -Version 0.1.0
```

Makes `artifacts\releases\Ampersite.AmperDock-win-Setup.exe` (installs for the current user, no admin). Installed copies update themselves
from this repository's GitHub releases; uninstalling first puts back cursors, folder icons and a theme pack's wallpaper.

## Test

```
dotnet test
```

## Layout

| folder | what |
| --- | --- |
| `src/AmperDock.Core` | database (SQLite), layout math. No UI, unit-tested |
| `src/AmperDock.Shell` | Windows integration: icons, shortcuts, running windows, launching |
| `src/AmperDock.Render` | the GPU dock renderer (Direct2D + DirectComposition) |
| `src/AmperDock.App` | the app: dock window, settings windows, theme, look packs |
| `tools` | the installer script |

Fonts: Nunito, SIL Open Font License 1.1 (`src/AmperDock.App/Assets/Fonts/OFL.txt`).
