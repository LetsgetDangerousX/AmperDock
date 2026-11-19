# AmperDock

A cute-but-clean dock for Windows 10/11, by Ampersite LLC.

Early development: milestone 0 (the vertical slice) works. You can pin apps by dropping them on the dock,
hover to magnify, click to launch, see paw-print running indicators, drag to reorder, and drag off to remove.

## Run it

Needs the .NET 10 SDK (`winget install Microsoft.DotNet.SDK.10`).

```
dotnet run --project src/AmperDock.App
```

- Right-click the dock to add apps or folders, change its position or appearance, or **Quit AmperDock**. You can also drag apps, files and folders from Explorer onto the dock.
- Move the dock by dragging an empty part of it (or Alt+drag anywhere on it): drop it near any screen edge to dock it there, or anywhere else to float.
- Settings live in `%APPDATA%\AmperDock\amperdock.db`. Use `--db <path>` to try it with a throwaway database.
- `--gallery` opens the developer component gallery (a test page for the UI building blocks, not settings).
- Set `AMPERDOCK_TRACE=1` to write a trace log to `%LOCALAPPDATA%\AmperDock\logs\`.

## Look packs (cursors, trails, folder icons)

Right-click the dock → **Look packs**. Cursors and folder icons change real Windows settings, and both can be
undone from the same window ("Your original cursors", and Restore next to each folder).

The art is original and lives in `replica/design/`. After changing it, rebuild the app's files with:

```
python replica/design/make_aesthetic_assets.py replica/design/assets
python tools/build_lookpacks.py
```

(`build_lookpacks.py` needs Pillow and uses Microsoft Edge to render.) `--selftest-looks report.txt` applies
and restores a cursor set and a folder icon on a temp folder, and checks everything came back exactly.

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
| `src/AmperDock.App` | the app: dock window, settings windows, theme |
| `replica/` | planning: recon, architecture, design system, build log |

Design tokens live in `replica/design/tokens.json`. After changing them, run
`python replica/design/gen_xaml.py replica/design/tokens.json src/AmperDock.App/Theme` and copy `tokens.json` into `src/AmperDock.App/`.

Fonts: Nunito, SIL Open Font License 1.1 (`src/AmperDock.App/Assets/Fonts/OFL.txt`).
