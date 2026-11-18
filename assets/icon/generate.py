#!/usr/bin/env python3
"""
Builds the Interface Sync icons from assets/icon/app.svg.

  app.ico            sizes 16, 20, 24, 32, 40, 48, 64, 256 (exe icon, Start menu, taskbar)
  tray-<state>.ico   sizes 16, 20, 24, 32, 40, 48 (notification area, 100-300% scaling)

Tray states differ by badge shape, not colour alone: check (in sync), three dots (syncing),
clock (waiting: game running), pause bars (paused), warning triangle (needs attention).

Each size is rendered from the SVG (no down-scaling of one bitmap) and stored as PNG inside the .ico.
Requires: pip install cairosvg pillow
"""
import io
import pathlib
import struct

import cairosvg
from PIL import Image

HERE = pathlib.Path(__file__).resolve().parent
OUT = HERE.parent.parent / "src" / "InterfaceSync.App" / "Icons"
MASTER = (HERE / "app.svg").read_text(encoding="utf-8")

APP_SIZES = [16, 20, 24, 32, 40, 48, 64, 256]
TRAY_SIZES = [16, 20, 24, 32, 40, 48]

RING = '<circle cx="47" cy="47" r="16" fill="#FFFFFF"/>'
BADGES = {
    # green disc with a check mark
    "insync": RING + '<circle cx="47" cy="47" r="13" fill="#1E8E3E"/>'
              '<path d="M40.5 47.5 L45 52 L54 42.5" fill="none" stroke="#FFFFFF" stroke-width="4" stroke-linecap="round" stroke-linejoin="round"/>',
    # blue disc with three dots
    "syncing": RING + '<circle cx="47" cy="47" r="13" fill="#1A73E8"/>'
               '<circle cx="40.5" cy="47" r="2.6" fill="#FFFFFF"/><circle cx="47" cy="47" r="2.6" fill="#FFFFFF"/>'
               '<circle cx="53.5" cy="47" r="2.6" fill="#FFFFFF"/>',
    # grey disc with clock hands
    "waiting": RING + '<circle cx="47" cy="47" r="13" fill="#5F6368"/>'
               '<path d="M47 39.5 L47 47 L52.5 50" fill="none" stroke="#FFFFFF" stroke-width="3.5" stroke-linecap="round" stroke-linejoin="round"/>',
    # grey disc with pause bars
    "paused": RING + '<circle cx="47" cy="47" r="13" fill="#5F6368"/>'
              '<rect x="41.5" y="40.5" width="4" height="13" rx="1" fill="#FFFFFF"/><rect x="48.5" y="40.5" width="4" height="13" rx="1" fill="#FFFFFF"/>',
    # red warning triangle with an exclamation mark (white outline keeps the shape on any taskbar)
    "attention": '<path d="M47 29.5 L63 60 L31 60 Z" fill="#FFFFFF" stroke="#FFFFFF" stroke-width="3" stroke-linejoin="round"/>'
                 '<path d="M47 33 L60 58 L34 58 Z" fill="#D93025" stroke="#D93025" stroke-width="1.5" stroke-linejoin="round"/>'
                 '<rect x="45.2" y="40" width="3.6" height="10" rx="1.2" fill="#FFFFFF"/><circle cx="47" cy="54" r="2.1" fill="#FFFFFF"/>',
}


def render(svg: str, size: int) -> bytes:
    png = cairosvg.svg2png(bytestring=svg.encode("utf-8"), output_width=size, output_height=size)
    # Normalise through Pillow so every entry is 32-bit RGBA PNG.
    img = Image.open(io.BytesIO(png)).convert("RGBA")
    buf = io.BytesIO()
    img.save(buf, format="PNG", optimize=True)
    return buf.getvalue()


def write_ico(path: pathlib.Path, svg: str, sizes: list[int]) -> None:
    images = [(s, render(svg, s)) for s in sizes]
    header = struct.pack("<HHH", 0, 1, len(images))
    entries, data = b"", b""
    offset = 6 + 16 * len(images)
    for size, png in images:
        dim = 0 if size >= 256 else size  # 0 means 256 in the ICO directory
        entries += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(png), offset + len(data))
        data += png
    path.write_bytes(header + entries + data)
    print(f"{path.relative_to(HERE.parent.parent)}: {', '.join(map(str, sizes))}")


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    write_ico(OUT / "app.ico", MASTER, APP_SIZES)
    for state, badge in BADGES.items():
        write_ico(OUT / f"tray-{state}.ico", MASTER.replace("<!-- BADGE -->", badge), TRAY_SIZES)
    # Previews for review (not used by the build).
    preview = HERE / "preview"
    preview.mkdir(exist_ok=True)
    for name, svg in [("app", MASTER)] + [(f"tray-{s}", MASTER.replace("<!-- BADGE -->", b)) for s, b in BADGES.items()]:
        for size in (16, 32, 256):
            (preview / f"{name}-{size}.png").write_bytes(render(svg, size))


if __name__ == "__main__":
    main()
