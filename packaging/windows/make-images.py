#!/usr/bin/env python3
"""Builds the installer's wizard images from assets/icon/app.svg (original artwork). Requires cairosvg, pillow."""
import io
import pathlib

import cairosvg
from PIL import Image

HERE = pathlib.Path(__file__).resolve().parent
CREST = (HERE.parent.parent / "assets" / "icon" / "app.svg").read_text(encoding="utf-8")
INNER = CREST.split(">", 1)[1].rsplit("</svg>", 1)[0]

PANEL = """<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" width="{w}" height="{h}" viewBox="0 0 164 314">
<defs><linearGradient id="bg" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#22335A"/><stop offset="1" stop-color="#0E1528"/></linearGradient>
<radialGradient id="glow" cx="0.5" cy="0.36" r="0.5"><stop offset="0" stop-color="#E7C676" stop-opacity="0.16"/><stop offset="1" stop-color="#E7C676" stop-opacity="0"/></radialGradient></defs>
<rect width="164" height="314" fill="url(#bg)"/><rect width="164" height="314" fill="url(#glow)"/>
<g transform="translate(37,72) scale(1.42)"><svg viewBox="0 0 64 64" width="64" height="64">{inner}</svg></g>
<rect x="22" y="196" width="120" height="1.6" fill="#C9A24E"/><path d="M82 192.6 L85.4 196.8 L82 201 L78.6 196.8 Z" fill="#E7C676"/>
<path d="M14 14 H70 M14 14 V50" stroke="#C9A24E" stroke-opacity="0.45" stroke-width="1" fill="none"/>
<path d="M150 300 H94 M150 300 V264" stroke="#C9A24E" stroke-opacity="0.45" stroke-width="1" fill="none"/>
</svg>"""

for scale, name in [(1, "100"), (2, "200")]:
    png = cairosvg.svg2png(bytestring=PANEL.format(w=164 * scale, h=314 * scale, inner=INNER).encode())
    Image.open(io.BytesIO(png)).convert("RGB").save(HERE / f"wizard-{name}.bmp")
    png = cairosvg.svg2png(bytestring=CREST.encode(), output_width=55 * scale, output_height=55 * scale)
    crest = Image.open(io.BytesIO(png)).convert("RGBA")
    white = Image.new("RGBA", crest.size, (255, 255, 255, 255))
    white.alpha_composite(crest)
    white.convert("RGB").save(HERE / f"small-{name}.bmp")
print("wizard-100/200.bmp, small-100/200.bmp")
