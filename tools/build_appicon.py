#!/usr/bin/env python3
"""Render AmperDock's app icon (replica/design/assets/app-icon.svg) to src/AmperDock.App/Assets/AmperDock.ico.

    python tools/build_appicon.py
"""
import sys
import tempfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from build_lookpacks import ASSETS, ROOT, svg_sheet, write_icon_file  # noqa: E402

SIZES = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]

with tempfile.TemporaryDirectory() as tmp:
    images = svg_sheet([(s, ASSETS / "app-icon.svg", s) for s in SIZES], Path(tmp))
out = ROOT / "src" / "AmperDock.App" / "Assets" / "AmperDock.ico"
write_icon_file(out, [images[s] for s in SIZES])
images[256].save(ROOT / "src" / "AmperDock.App" / "Assets" / "AmperDock.png")
print("wrote", out)
