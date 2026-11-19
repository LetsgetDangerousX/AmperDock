#!/usr/bin/env python3
"""Build AmperDock's dock themes.

    python replica/design/make_dock_themes.py   # (re)draw the art
    python tools/build_dockthemes.py             # render it for the app

Reads   replica/design/assets/dock-*.svg, ind-*.svg
Writes  src/AmperDock.App/DockThemes/
          decor/<name>.png        256px decorations
          indicators/<name>.png   96px running indicators
          previews/<theme>.png    the cards in Dock appearance
          dockthemes.json         the catalogue the app reads

A theme is a starting look (colours, shape, indicator) plus decorations. Picking one fills in the
Appearance window; the person can still change any colour afterwards. Decoration placement:
  from     "start" | "end"     which end of the dock it is measured from
  offset   band heights        along the dock from that end (negative = past the end)
  edge     "top" | "bottom"    the dock's far edge, or the edge against the screen
  dy       band heights        extra shift across the dock (negative = away from the screen)
  anchor   [x, y] 0..1         the point of the picture that sits on that spot
  size     band heights        picture size (pictures are square)
  behind   bool                drawn under the dock, so it peeks out from behind
  upright  bool                stays upright on side docks (else turns with the dock)
  mirror   bool                flipped left-right
Attachments are shapes that grow out of the dock itself, painted like it and sharing its outline:
  kind     "cup" | "ring" | "moon"
  from     "start" | "end"
  along    band heights        where its centre is, from that end (cup and ring)
  size     band heights        its radius (the planet's, for a ring)
  sprite   the planet (cup, ring) or the face (moon)
  color    the moon's paint
room_start / room_end (band heights) lengthen the dock at that end so attachments don't cover icons.
All art is original, (c) Ampersite LLC.
"""
import json
import shutil
import sys
import tempfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from build_lookpacks import ASSETS, render  # noqa: E402

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "src" / "AmperDock.App" / "DockThemes"

DECOR = ["dock-shrooms", "dock-holly", "dock-pumpkin", "dock-bat", "dock-acorn", "dock-sakura",
         "planet-ball", "planet-earthy", "planet-sunny", "moon-face", "ind-twinkle"]
# toned copies: the same art pulled toward a dock's colours, so it reads as part of that dock
TONED = {
    "planet-moon.dusk": ("planet-moon", "saturate(.75) hue-rotate(-18deg) brightness(.82) contrast(.95)"),
    "planet-rosy.dusk": ("planet-rosy", "saturate(.7) hue-rotate(-28deg) brightness(.8) contrast(.95)"),
}
INDICATORS = ["mushroom", "star", "leaf", "pumpkin", "petal", "heart", "planet", "moon", "twinkle"]


def D(sprite, frm, offset, edge, anchor, size, behind=False, upright=True, mirror=False, dy=0.0):
    return dict(sprite=sprite, **{"from": frm}, offset=offset, edge=edge, dy=dy, anchor=anchor, size=size,
                behind=behind, upright=upright, mirror=mirror)


def A(kind, frm, along, size, sprite=None, color=None):
    return dict(kind=kind, **{"from": frm}, along=along, size=size, sprite=sprite, color=color)


THEMES = [
    dict(id="lilac", name="Lilac", season=None, blurb="Frosted lilac glass. Cute only in the details.",
         look=dict(style="glass", opacity=78, outline=True, outline_width=1, roundness=0.5, indicator="paw"),
         ears=False, snow=False, decor=[]),
    dict(id="purrple", name="Purrple", season=None, blurb="Violet glass with cat ears on top.",
         look=dict(style="glass", color="#C7B0F7", opacity=84, outline=True, outline_color="#9C7BE6", outline_width=1.5,
                   roundness=0.5, indicator="paw", indicator_color="#7444D6"),
         ears=True, snow=False, decor=[]),
    dict(id="mushroom", name="Mushroom Grove", season=None, blurb="Warm cream, a mossy edge and a toadstool on the corner.",
         look=dict(style="solid", color="#FFF6E8", opacity=90, outline=True, outline_color="#9DBF8A", outline_width=2,
                   roundness=0.5, indicator="mushroom"),
         ears=False, snow=False, decor=[D("dock-shrooms", "start", -0.22, "bottom", [0.36, 0.95], 0.88)]),
    dict(id="midnight", name="Midnight", season=None, blurb="Dark plum, no decorations. For when you want it plain.",
         look=dict(style="solid", color="#2A1F3D", opacity=90, outline=True, outline_color="#4A3A66", outline_width=1,
                   roundness=0.5, indicator="dot", indicator_color="#B497FF"),
         ears=False, snow=False, decor=[]),
    dict(id="snowy-pine", name="Snowy Pine", season="Christmas", blurb="Pine green with a snow cap and a sprig of holly.",
         look=dict(style="gradient", color="#2F6B4F", color2="#3F8C63", gradient_angle=90, opacity=94, outline=True,
                   outline_color="#22503A", outline_width=1, roundness=0.5, indicator="star"),
         ears=False, snow=True, decor=[D("dock-holly", "end", 0.62, "top", [0.5, 0.56], 0.62, upright=False)]),
    dict(id="autumn-leaf", name="Autumn Leaf", season="Autumn", blurb="A leaf-shaped dock with veins, a stem and an acorn friend.",
         look=dict(style="gradient", color="#F6B25E", color2="#E0673A", gradient_angle=0, opacity=95, outline=True,
                   outline_color="#B5502A", outline_width=1.5, roundness=0.5, shape="leaf", indicator="leaf"),
         ears=False, snow=False, decor=[D("dock-acorn", "start", -0.05, "top", [0.5, 0.9], 0.55, dy=0.2)]),
    dict(id="pumpkin-night", name="Pumpkin Night", season="Halloween", blurb="Dusky purple, a jack-o'-lantern peeking over, and a tiny bat.",
         look=dict(style="glass", color="#3B2557", opacity=90, outline=True, outline_color="#6B4A9A", outline_width=1.5,
                   roundness=0.5, indicator="pumpkin"),
         ears=False, snow=False, decor=[D("dock-pumpkin", "end", 0.8, "top", [0.5, 0.74], 0.78, behind=True),
                                        D("dock-bat", "start", 0.55, "top", [0.5, 0.55], 0.5, dy=-0.5)]),
    dict(id="sakura-breeze", name="Sakura Breeze", season="Spring", blurb="Blossom pink with a cherry branch on the corner.",
         look=dict(style="gradient", color="#FFE3EE", color2="#FFC6DD", gradient_angle=90, opacity=92, outline=True,
                   outline_color="#F4A6C8", outline_width=1.5, roundness=0.5, indicator="petal"),
         ears=False, snow=False, decor=[D("dock-sakura", "start", 0.12, "top", [0.2, 0.8], 0.85, upright=False, dy=0.22)]),
    dict(id="starlight", name="Starlight", season=None, blurb="A night-sky dock that is a planet's ring, with little planets rising behind.",
         look=dict(style="gradient", color="#2B2150", color2="#55387F", gradient_angle=0, opacity=96, outline=True,
                   outline_color="#9C86E6", outline_width=1.5, roundness=0.5, indicator="planet"),
         ears=False, snow=False, room_start=1.75,
         attach=[A("ring", "start", 1.12, 0.55, "planet-ball")],
         decor=[D("planet-moon.dusk", "end", 1.55, "top", [0.5, 0.6], 0.62, behind=True),
                D("planet-rosy.dusk", "end", 0.7, "top", [0.5, 0.64], 0.5, behind=True)]),
    dict(id="moonbeam", name="Moonbeam", season=None, blurb="Deep blue, and the end of the dock is a sleepy crescent moon.",
         look=dict(style="gradient", color="#1F2A5A", color2="#36468A", gradient_angle=0, opacity=96, outline=True,
                   outline_color="#8FA2E6", outline_width=1.5, roundness=0.5, indicator="moon"),
         ears=False, snow=False, room_end=0.5,
         attach=[A("moon", "end", 0, 0.56, "moon-face", "#FFE9A8")],
         decor=[D("ind-twinkle", "start", 0.45, "top", [0.5, 0.5], 0.3, dy=-0.18),
                D("ind-twinkle", "start", 0.95, "top", [0.5, 0.5], 0.2, dy=-0.34)]),
    dict(id="planet-nest", name="Planet Nest", season=None, blurb="A daytime sky dock with a planet tucked into a cup, and the sun coming up.",
         look=dict(style="gradient", color="#DDF0FF", color2="#FFE3F1", gradient_angle=0, opacity=94, outline=True,
                   outline_color="#A9C8EE", outline_width=1.5, roundness=0.5, indicator="twinkle"),
         ears=False, snow=False, room_start=1.5,
         attach=[A("cup", "start", 0.75, 0.62, "planet-earthy")],
         decor=[D("planet-sunny", "end", 0.75, "top", [0.5, 0.68], 0.9, behind=True)]),
]


# ---------------------------------------------------------------- previews (the same rules the app uses)

def band_path(x, y, L, H, leaf, r):
    if not leaf:
        return (f"M{x+r} {y}H{x+L-r}A{r} {r} 0 0 1 {x+L} {y+r}V{y+H-r}A{r} {r} 0 0 1 {x+L-r} {y+H}"
                f"H{x+r}A{r} {r} 0 0 1 {x} {y+H-r}V{y+r}A{r} {r} 0 0 1 {x+r} {y}Z")
    e = 0.42 * H
    a = min(0.07 * H, L * 0.02)
    P = lambda px, py: f"{x+px:.2f} {y+py:.2f}"  # noqa: E731
    return (f"M{P(-e, .5*H)}C{P(-e*.55, .12*H)} {P(-.05*H, 0)} {P(.3*H, 0)}"
            f"C{P(L*.3, -a)} {P(L*.7, -a)} {P(L-.3*H, 0)}C{P(L+.05*H, 0)} {P(L+e*.55, .1*H)} {P(L+e, .45*H)}"
            f"C{P(L+e*.5, .85*H)} {P(L+.05*H, H)} {P(L-.3*H, H)}L{P(.3*H, H)}C{P(-.05*H, H)} {P(-e*.55, .88*H)} {P(-e, .5*H)}Z")


def ears_svg(x, y, L, H, fill, op, stroke, sw):
    out = []
    for c in (min(0.95 * H, L * 0.25), max(L - 0.95 * H, L * 0.75)):
        for side in (-1, 1):
            inner, outer, tx, ty = c + side * .06 * H, c + side * .40 * H, c + side * .27 * H, -.30 * H
            sides = (f"M{x+inner} {y+.04*H}L{x+tx-side*.05*H} {y+ty+.06*H}Q{x+tx} {y+ty-.02*H} {x+tx+side*.05*H} {y+ty+.06*H}"
                     f"L{x+outer} {y+.04*H}")
            out.append(f'<path d="{sides}Z" fill="{fill}" fill-opacity="{op}"/>')
            out.append(f'<path d="M{x+c+side*.13*H} {y+.03*H}L{x+tx+side*.005*H} {y+ty+.13*H}L{x+c+side*.33*H} {y+.03*H}Z" fill="#F4A6C8"/>')
            out.append(f'<path d="{sides}" fill="none" stroke="{stroke}" stroke-width="{sw}" stroke-linejoin="round" stroke-linecap="round"/>')
    return "".join(out)


def snow_svg(x, y, L, H, clip_id):  # noqa: C901
    def cap(extra):
        d = f"M{x-H} {y-H}H{x+L+H}V{y+.24*H+extra}"
        cx, i = L + H * .2, 0
        while cx > -H * .2:
            w = H * (.42 + .12 * ((i * 7) % 3))
            depth = H * (.2 + .06 * ((i * 5) % 3)) + extra
            d += f"Q{x+cx-w/2} {y+depth+.16*H} {x+cx-w} {y+depth}"
            if i % 4 == 2:
                d += f"Q{x+cx-w-.03*H} {y+depth+.22*H} {x+cx-w-.07*H} {y+depth}"
                cx -= .07 * H
            cx -= w
            i += 1
        return d + f"H{x-H}Z"
    return (f'<g clip-path="url(#{clip_id})"><path d="{cap(.04*H)}" fill="#CCE0F7"/><path d="{cap(0)}" fill="#fff"/></g>')


def decor_box(d, bx, by, L, H):
    along = bx + d["offset"] * H if d["from"] == "start" else bx + L - d["offset"] * H
    across = (by if d["edge"] == "top" else by + H) + d["dy"] * H
    size = d["size"] * H
    ax = 1 - d["anchor"][0] if d["mirror"] else d["anchor"][0]
    return along - ax * size, across - d["anchor"][1] * size, size


def preview_html(t, uid):
    W, Hc = 240, 108
    H, icon, gap, pad = 44, 30, 7, 10
    rs, re = t.get("room_start", 0), t.get("room_end", 0)
    L = 3 * icon + 2 * gap + 2 * pad + (rs + re) * H
    moon_room = max((1.3 * a["size"] * H for a in t.get("attach", []) if a["kind"] == "moon"), default=0)
    bx, by = (W - L - moon_room) / 2, Hc - H - 8
    lk = t["look"]
    leaf = lk.get("shape") == "leaf"
    fill, fill2 = lk.get("color", "#E9DDFB"), lk.get("color2")
    op = lk["opacity"] / 100
    stroke = lk.get("outline_color", "#D5C3F2")
    sw = lk.get("outline_width", 1)
    grad = ""
    paint = fill
    if lk["style"] == "gradient":
        ang = lk.get("gradient_angle", 0)
        x2, y2 = (1, 0) if ang == 0 else (0, 1)
        grad = f'<linearGradient id="g{uid}" x1="0" y1="0" x2="{x2}" y2="{y2}"><stop offset="0" stop-color="{fill}"/><stop offset="1" stop-color="{fill2}"/></linearGradient>'
        paint = f"url(#g{uid})"
    path = band_path(bx, by, L, H, leaf, H / 2)
    parts = [f'<defs>{grad}<clipPath id="c{uid}"><path d="{path}"/></clipPath></defs>']
    imgs_behind, imgs_front = [], []
    for d in t["decor"]:
        x, y, s = decor_box(d, bx, by, L, H)
        flip = "transform:scaleX(-1);" if d["mirror"] else ""
        name, tone = TONED.get(d["sprite"], (d["sprite"], ""))
        tone = f"filter:{tone};" if tone else ""
        tag = f'<img src="{(ASSETS / (name + ".svg")).as_uri()}" style="position:absolute;left:{x}px;top:{y}px;width:{s}px;height:{s}px;{flip}{tone}">'
        (imgs_behind if d["behind"] else imgs_front).append(tag)
    attach = t.get("attach", [])
    href = lambda n: (ASSETS / f"{n}.svg").as_uri()  # noqa: E731
    shapes, after_fill, outline_extra, after_outline = [], [], [], []
    for a in attach:
        if a["kind"] == "ring":
            pr = a["size"] * H
            cx = bx + a["along"] * H if a["from"] == "start" else bx + L - a["along"] * H
            rx, ry, yc, w = 1.95 * pr, .6 * pr, by + .06 * H, .4 * pr
            arc = f"M{cx-rx} {yc}A{rx} {ry} 0 0 1 {cx+rx} {yc}"
            parts.append(f'<path d="{arc}" fill="none" stroke="{stroke}" stroke-width="{w+2*sw}" stroke-linecap="round"/>'
                         f'<path d="{arc}" fill="none" stroke="{paint}" stroke-opacity="{op}" stroke-width="{w}" stroke-linecap="round"/>')
            s2 = 2.29 * pr
            parts.append(f'<image href="{href(a["sprite"])}" x="{cx-s2/2}" y="{by-.25*pr-s2/2}" width="{s2}" height="{s2}"/>')
        elif a["kind"] == "cup":
            r = a["size"] * H
            cx = bx + a["along"] * H if a["from"] == "start" else bx + L - a["along"] * H
            cy = by + .02 * H
            shapes.append(f'<circle cx="{cx}" cy="{cy}" r="{r}"/>')
            s2 = 2.56 * r
            after_fill.append(("cup", f'<circle cx="{cx}" cy="{cy}" r="{r}" fill="{paint}" fill-opacity="{op}"/>'
                                      f'<image href="{href(a["sprite"])}" x="{cx-s2/2}" y="{cy-.45*r-s2/2}" width="{s2}" height="{s2}" clip-path="url(#above{uid})"/>'))
            parts.insert(0, f'<defs><clipPath id="above{uid}"><rect x="0" y="0" width="{W}" height="{by}"/></clipPath></defs>')
            outline_extra.append(f'<path d="M{cx-r*.96} {by}H{cx+r*.96}" stroke="{stroke}" stroke-opacity=".55" stroke-width="{sw*.9}" stroke-linecap="round"/>')
        elif a["kind"] == "moon":
            r = a["size"] * H
            cx, cy = bx + L + .3 * r, by + .46 * H
            ix, iy, ir = cx + .45 * r, cy - .12 * r, .82 * r
            mask = f"m{uid}"
            parts.insert(0, f'<defs><mask id="{mask}"><circle cx="{cx}" cy="{cy}" r="{r+sw}" fill="#fff"/><circle cx="{ix}" cy="{iy}" r="{ir-sw}" fill="#000"/></mask>'
                            f'<mask id="{mask}f"><circle cx="{cx}" cy="{cy}" r="{r}" fill="#fff"/><circle cx="{ix}" cy="{iy}" r="{ir}" fill="#000"/></mask></defs>')
            shapes.append(f'<circle cx="{cx}" cy="{cy}" r="{r}" mask="url(#{mask})"/>')
            after_fill.append(("moon", f'<circle cx="{cx}" cy="{cy}" r="{r}" fill="{a["color"] or "#FFE9A8"}" mask="url(#{mask}f)"/>'))
            fs = .62 * r
            after_outline.append(f'<image href="{href(a["sprite"])}" x="{cx-.66*r-fs/2}" y="{by+.5*H-fs/2}" width="{fs}" height="{fs}"/>')
    if shapes:
        # one outline round the band and everything joined to it: stroke them all, then paint over the inner lines
        parts.append(f'<g fill="none" stroke="{stroke}" stroke-width="{2*sw}" stroke-linejoin="round"><path d="{path}"/>{"".join(shapes)}</g>')
        parts.extend(svg for kind, svg in after_fill if kind == "cup")
    if t["ears"]:
        parts.append(ears_svg(bx, by, L, H, fill, op, stroke, sw))
    parts.append(f'<path d="{path}" fill="{paint}" fill-opacity="{op}" stroke="none"/>')
    if shapes:
        parts.extend(svg for kind, svg in after_fill if kind == "moon")
    if t["snow"]:
        parts.append(snow_svg(bx, by, L, H, f"c{uid}"))
    if leaf:
        e = .42 * H
        parts.append(f'<path d="M{bx-e*.6} {by+.5*H}C{bx+L*.3} {by+.56*H} {bx+L*.7} {by+.44*H} {bx+L+e*.7} {by+.46*H}" stroke="{stroke}" stroke-opacity=".4" stroke-width="1.6" fill="none"/>')
        parts.append(f'<path d="M{bx+L+e*.92} {by+.45*H}C{bx+L+e+.12*H} {by+.42*H} {bx+L+e+.24*H} {by+.33*H} {bx+L+e+.3*H} {by+.16*H}" stroke="{stroke}" stroke-width="3" stroke-linecap="round" fill="none"/>')
    if not shapes:
        parts.append(f'<path d="{path}" fill="none" stroke="{stroke}" stroke-width="{sw}"/>')
    parts.extend(outline_extra + after_outline)
    colours = ["#B497FF", "#F4A6C8", "#7ED9AE"]
    for i, c in enumerate(colours):
        ix = bx + rs * H + pad + i * (icon + gap)
        iy = by + (H - icon) / 2 - 2
        parts.append(f'<rect x="{ix}" y="{iy}" width="{icon}" height="{icon}" rx="8" fill="{c}"/>'
                     f'<rect x="{ix+4}" y="{iy+3}" width="{icon-8}" height="5" rx="2.5" fill="#fff" opacity=".45"/>')
    ind = lk.get("indicator", "paw")
    ix, iy = bx + rs * H + pad + icon / 2, by + H - 6
    if ind in INDICATORS:
        imgs_front.append(f'<img src="{(ASSETS / f"ind-{ind}.svg").as_uri()}" style="position:absolute;left:{ix-6}px;top:{iy-6}px;width:12px;height:12px">')
    elif ind == "dot":
        parts.append(f'<circle cx="{ix}" cy="{iy}" r="2.4" fill="{lk.get("indicator_color", "#7444D6")}"/>')
    else:
        parts.append(f'<g transform="translate({ix-5} {iy-5}) scale({10/24})" fill="{lk.get("indicator_color", "#7444D6")}">'
                     '<ellipse cx="12" cy="16.2" rx="5.6" ry="4.6"/><ellipse cx="5.2" cy="10.4" rx="2.2" ry="2.7"/>'
                     '<ellipse cx="9.4" cy="6.2" rx="2.3" ry="2.9"/><ellipse cx="14.6" cy="6.2" rx="2.3" ry="2.9"/><ellipse cx="18.8" cy="10.4" rx="2.2" ry="2.7"/></g>')
    svg = f'<svg width="{W}" height="{Hc}" style="position:absolute;left:0;top:0" xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink">{"".join(parts)}</svg>'
    return "".join(imgs_behind) + svg + "".join(imgs_front), W, Hc


def main():
    work = Path(tempfile.mkdtemp(prefix="amperdock-themes-"))
    try:
        if OUT.exists():
            shutil.rmtree(OUT)
        for sub in ("decor", "indicators", "previews"):
            (OUT / sub).mkdir(parents=True)

        # sprites: one sheet, 256px decorations then 96px indicators
        items = [(n, ASSETS / f"{n}.svg", 256, OUT / "decor" / f"{n.removeprefix('dock-')}.png", "") for n in DECOR]
        items += [(n, ASSETS / f"{src}.svg", 256, OUT / "decor" / f"{n}.png", tone) for n, (src, tone) in TONED.items()]
        items += [(n, ASSETS / f"ind-{n}.svg", 96, OUT / "indicators" / f"{n}.png", "") for n in INDICATORS]
        x, html = 0, []
        for _, src, size, _, tone in items:
            html.append(f'<img src="{src.as_uri()}" style="position:absolute;left:{x}px;top:0;width:{size}px;height:{size}px;{f"filter:{tone}" if tone else ""}">')
            x += size + 4
        sheet = render(f'<html><body style="margin:0;background:transparent">{"".join(html)}</body></html>', x, 256, work)
        x = 0
        for _, _, size, dest, _ in items:
            sheet.crop((x, 0, x + size, size)).save(dest)
            x += size + 4

        # preview cards, rendered at 2x for crisp cards on high-DPI screens
        rows = []
        for i, t in enumerate(THEMES):
            body, w, h = preview_html(t, i)
            rows.append(f'<div style="position:absolute;left:0;top:{i*h}px;width:{w}px;height:{h}px">{body}</div>')
        sheet = render(f'<html><body style="margin:0;background:transparent;zoom:2">{"".join(rows)}</body></html>',
                       480, 216 * len(THEMES), work)
        cards = [sheet.crop((0, i * 216, 480, (i + 1) * 216)) for i in range(len(THEMES))]
        # one shared crop box (the union of what every card draws, plus a margin) keeps the docks the same size
        boxes = [c.getbbox() for c in cards if c.getbbox()]
        l, t_, r, b = min(x[0] for x in boxes), min(x[1] for x in boxes), max(x[2] for x in boxes), max(x[3] for x in boxes)
        box = (max(0, l - 8), max(0, t_ - 8), min(480, r + 8), min(216, b + 8))
        for card, t in zip(cards, THEMES):
            card.crop(box).save(OUT / "previews" / f"{t['id']}.png")

        catalogue = []
        for t in THEMES:
            look = dict(t["look"], theme=t["id"], decorations=True)
            look.setdefault("shape", "rounded")
            catalogue.append(dict(id=t["id"], name=t["name"], season=t["season"], blurb=t["blurb"],
                                  preview=f"previews/{t['id']}.png", look=look, ears=t["ears"], snow=t["snow"],
                                  decor=[dict(d, sprite=f"decor/{d['sprite'].removeprefix('dock-')}.png") for d in t["decor"]],
                                  attach=[dict(a, sprite=f"decor/{a['sprite']}.png" if a["sprite"] else None) for a in t.get("attach", [])],
                                  room_start=t.get("room_start", 0), room_end=t.get("room_end", 0)))
        data = dict(indicators={n: f"indicators/{n}.png" for n in INDICATORS}, themes=catalogue)
        (OUT / "dockthemes.json").write_text(json.dumps(data, indent=2), encoding="utf-8")
        print(f"{len(THEMES)} themes, {len(DECOR)} decorations, {len(INDICATORS)} indicators -> {OUT}")
    finally:
        shutil.rmtree(work, ignore_errors=True)


if __name__ == "__main__":
    main()
