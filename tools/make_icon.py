"""Draws the app icon (a viewfinder with a red record light in the middle) into
app/CamRemote/CamRemote.ico, app/CamRemote/phone-icon.png and the iOS AppIcon.

Usage: python -I tools/make_icon.py   (needs Pillow)
The icon is laid out on a 1024 grid. Each size is drawn larger and scaled down, so edges stay clean.
At 64 px and below a heavier drawing is used: a thicker frame and a bigger record light.
"""
from pathlib import Path
from PIL import Image, ImageDraw

SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]
ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "app" / "CamRemote" / "CamRemote.ico"
IOS = ROOT / "app" / "CamRemote.iOS" / "Sources" / "Assets.xcassets" / "AppIcon.appiconset" / "icon.png"

TILE = (16, 18, 20)  # the app's page colour, #101214
WHITE = (245, 246, 244)
REC = (229, 50, 45)  # same red as the app's record button


def stroke(d, points, width, k, fill):
    """A polyline with round caps and joins, in grid units scaled by k."""
    p = [(x * k, y * k) for x, y in points]
    d.line(p, fill=fill, width=round(width * k))
    r = width * k / 2
    for x, y in p:
        d.ellipse([x - r, y - r, x + r, y + r], fill=fill)


def draw(size, tile=True):
    small = size <= 64
    ss = max(2, min(8, 4096 // size))
    s = size * ss
    k = s / 1024
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    if tile:  # rounded tile, as the Windows icon has always had
        d.rounded_rectangle([0, 0, s - 1, s - 1], radius=s * 56 / 256, fill=TILE)
    else:  # full-bleed square for iOS, which rounds the corners itself
        d.rectangle([0, 0, s, s], fill=TILE)

    # Viewfinder corners.
    m, arm, w = (196, 168, 76) if small else (232, 150, 50)
    for sx, sy in [(1, 1), (-1, 1), (1, -1), (-1, -1)]:
        x = m if sx == 1 else 1024 - m
        y = m if sy == 1 else 1024 - m
        stroke(d, [(x, y + sy * arm), (x, y), (x + sx * arm, y)], w, k, WHITE)

    # Record light, in the middle of the frame.
    r = 150 if small else 112
    d.ellipse([(512 - r) * k, (512 - r) * k, (512 + r) * k, (512 + r) * k], fill=REC)
    return img.resize((size, size), Image.LANCZOS)


images = [draw(n) for n in SIZES]
images[-1].save(OUT, format="ICO", sizes=[(n, n) for n in SIZES], append_images=images[:-1])
phone = Image.new("RGB", (180, 180), (0, 0, 0))  # iPhone home screen, and the Mac icon via build-mac.sh
icon = draw(180)
phone.paste(icon, (0, 0), icon)
phone.save(OUT.with_name("phone-icon.png"))
draw(1024, tile=False).convert("RGB").save(IOS)  # no alpha: App Store icons must be opaque
print(f"wrote {OUT}, phone-icon.png and the iOS AppIcon")
