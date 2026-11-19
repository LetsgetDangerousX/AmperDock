# AmperDock

A cute-but-clean dock for Windows 10/11, by Ampersite LLC.

Early development: milestone 0 (the vertical slice) works. You can pin apps by dropping them on the dock,
hover to magnify, click to launch, see paw-print running indicators, drag to reorder, and drag off to remove.

## Run it

Needs the .NET 10 SDK (`winget install Microsoft.DotNet.SDK.10`).

```
dotnet run --project src/AmperDock.App
```

- Right-click the dock for the component gallery and **Quit AmperDock**.
- Settings live in `%APPDATA%\AmperDock\amperdock.db`. Use `--db <path>` to try it with a throwaway database.
- `--gallery` opens the component gallery at startup.
- Set `AMPERDOCK_TRACE=1` to write a trace log to `%LOCALAPPDATA%\AmperDock\logs\`.

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
