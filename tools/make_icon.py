"""Draws the app icon (an egret in a viewfinder, with a red record light) into
app/CamRemote/CamRemote.ico, app/CamRemote/phone-icon.png and the iOS AppIcon.

Usage: python -I tools/make_icon.py   (needs Pillow)
The icon is laid out on a 1024 grid. Each size is drawn larger and scaled down, so edges stay clean.
At 64 px and below a heavier drawing is used: a thicker frame and legs, and a bigger record light.
"""
import math
import re
from pathlib import Path
from PIL import Image, ImageDraw

SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]
ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "app" / "CamRemote" / "CamRemote.ico"
IOS = ROOT / "app" / "CamRemote.iOS" / "Sources" / "Assets.xcassets" / "AppIcon.appiconset" / "icon.png"

TILE = (16, 18, 20)  # the app's page colour, #101214
WHITE = (245, 246, 244)
REC = (229, 50, 45)  # same red as the app's record button

# Bill, head, neck, body and tail as one outline, facing right.
BIRD = ("M792 298 L632 274 C608 270 588 262 576 278 C566 288 560 304 552 322 "
        "C536 356 536 398 556 432 C572 458 574 478 556 492 "
        "C520 520 420 530 330 604 C290 638 258 676 222 726 "
        "C282 692 322 668 362 652 C424 632 520 614 578 582 "
        "C622 556 642 520 630 486 C620 452 600 430 588 410 "
        "C576 388 574 362 584 340 C590 326 596 318 608 312 C616 308 624 306 636 306 Z")
LEGS = [[(482, 612), (474, 824)], [(530, 600), (582, 694), (540, 744)]]


def outline(d, steps=24):
    """Points along an SVG path made of M, L, C and Z."""
    t = re.findall(r"[A-Za-z]|-?\d+(?:\.\d+)?", d)
    pts, i, cmd = [], 0, None
    while i < len(t):
        if t[i].isalpha():
            cmd = t[i]
            i += 1
            continue
        if cmd in "ML":
            pts.append((float(t[i]), float(t[i + 1])))
            i += 2
        elif cmd == "C":
            p0 = pts[-1]
            p1, p2, p3 = [(float(t[i + k]), float(t[i + k + 1])) for k in (0, 2, 4)]
            for n in range(1, steps + 1):
                s = n / steps
                u = 1 - s
                pts.append(tuple(u ** 3 * a + 3 * u * u * s * b + 3 * u * s * s * c + s ** 3 * e
                                 for a, b, c, e in zip(p0, p1, p2, p3)))
            i += 6
    return pts


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
    m, arm, w = (160, 140, 52) if small else (176, 112, 26)
    for sx, sy in [(1, 1), (-1, 1), (1, -1), (-1, -1)]:
        x = m if sx == 1 else 1024 - m
        y = m if sy == 1 else 1024 - m
        stroke(d, [(x, y + sy * arm), (x, y), (x + sx * arm, y)], w, k, WHITE)

    # The egret, nudged left of centre (and enlarged a little for small sizes).
    dx, dy, scale = (-16, 4, 1.1) if small else (-22, 0, 1.0)

    def place(p):
        return [((x - 512) * scale + 512 + dx, (y - 540) * scale + 540 + dy) for x, y in p]

    d.polygon([(x * k, y * k) for x, y in place(outline(BIRD))], fill=WHITE)
    a = math.radians(10)  # the head: a slightly tilted oval
    head = [(596 + 33 * math.cos(t) * math.cos(a) - 26 * math.sin(t) * math.sin(a),
             288 + 33 * math.cos(t) * math.sin(a) + 26 * math.sin(t) * math.cos(a))
            for t in (2 * math.pi * n / 64 for n in range(64))]
    d.polygon([(x * k, y * k) for x, y in place(head)], fill=WHITE)
    for leg in LEGS:
        stroke(d, place(leg), (30 if small else 13) * scale, k, WHITE)

    # Record light.
    cx, r = (774, 40) if small else (778, 24)
    d.ellipse([(cx - r) * k, (cx - r) * k, (cx + r) * k, (cx + r) * k], fill=REC)
    return img.resize((size, size), Image.LANCZOS)


images = [draw(n) for n in SIZES]
images[-1].save(OUT, format="ICO", sizes=[(n, n) for n in SIZES], append_images=images[:-1])
phone = Image.new("RGB", (180, 180), (0, 0, 0))  # iPhone home screen, and the Mac icon via build-mac.sh
icon = draw(180)
phone.paste(icon, (0, 0), icon)
phone.save(OUT.with_name("phone-icon.png"))
draw(1024, tile=False).convert("RGB").save(IOS)  # no alpha: App Store icons must be opaque
print(f"wrote {OUT}, phone-icon.png and the iOS AppIcon")
