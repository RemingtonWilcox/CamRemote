"""Draws the app icon (a camera with a red record light) into app/CamRemote/CamRemote.ico.

Usage: python -I tools/make_icon.py   (needs Pillow)
Each size is drawn at 8x and scaled down, so edges stay clean. Below 48 px the Wi-Fi arcs are left out.
"""
from pathlib import Path
from PIL import Image, ImageDraw

SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]
OUT = Path(__file__).resolve().parent.parent / "app" / "CamRemote" / "CamRemote.ico"

TILE_TOP, TILE_BOTTOM = (46, 52, 61), (20, 23, 28)
BODY = (236, 236, 236)
LENS_RING, LENS_GLASS, GLINT = (20, 23, 28), (52, 74, 102), (170, 200, 230)
REC = (229, 50, 45)  # same red as the app's record button


def draw(size):
    s = size * 8
    u = s / 256  # design units: the icon is laid out on a 256 grid
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))

    # Rounded tile with a soft top-to-bottom gradient.
    grad = Image.new("RGBA", (s, s))
    gd = ImageDraw.Draw(grad)
    for y in range(s):
        t = y / (s - 1)
        gd.line([(0, y), (s, y)], fill=tuple(round(a + (b - a) * t) for a, b in zip(TILE_TOP, TILE_BOTTOM)) + (255,))
    mask = Image.new("L", (s, s), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, s - 1, s - 1], radius=56 * u, fill=255)
    img.paste(grad, (0, 0), mask)

    d = ImageDraw.Draw(img)
    small = size < 48
    top = 92 if not small else 74  # leave room for the Wi-Fi arcs on big sizes

    # Camera body with the viewfinder hump.
    d.rounded_rectangle([40 * u, (top + 20) * u, 216 * u, (top + 128) * u], radius=22 * u, fill=BODY)
    d.rounded_rectangle([96 * u, (top + 2) * u, 160 * u, (top + 40) * u], radius=10 * u, fill=BODY)

    # Lens.
    cx, cy = 128 * u, (top + 76) * u
    for r, c in ((46, LENS_RING), (34, LENS_GLASS)):
        d.ellipse([cx - r * u, cy - r * u, cx + r * u, cy + r * u], fill=c)
    if not small:
        d.ellipse([cx - 22 * u, cy - 22 * u, cx - 8 * u, cy - 8 * u], fill=GLINT)

    # Record light.
    r = 13 if not small else 16
    rx, ry = 190 * u, (top + 44) * u
    d.ellipse([rx - r * u, ry - r * u, rx + r * u, ry + r * u], fill=REC)

    # Wi-Fi arcs above the camera.
    if not small:
        for radius, width in ((56, 11), (34, 11)):
            box = [128 * u - radius * u, 86 * u - radius * u, 128 * u + radius * u, 86 * u + radius * u]
            d.arc(box, start=225, end=315, fill=BODY, width=round(width * u))
        d.ellipse([128 * u - 8 * u, 78 * u - 8 * u, 128 * u + 8 * u, 78 * u + 8 * u], fill=BODY)

    return img.resize((size, size), Image.LANCZOS)


images = [draw(n) for n in SIZES]
images[-1].save(OUT, format="ICO", sizes=[(n, n) for n in SIZES], append_images=images[:-1])
print(f"wrote {OUT}")
