"""Generates src/AecAudioRouter/Assets/AppIcon.ico (concept B: microphone + blocked echo).

Run from the repository root:  python assets/make_icon.py
Requires Pillow. Shapes are drawn in a 128-unit design grid, supersampled, then downscaled.
Sizes up to 24 px use a simplified variant with thicker strokes so the glyph stays legible.
"""
import math
from pathlib import Path
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
ICO = ROOT / "src" / "AecAudioRouter" / "Assets" / "AppIcon.ico"
PREVIEW = ROOT / "assets" / "AppIcon-preview.png"
SIZES = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
SUPER = 8

ACCENT = (0x83, 0xE2, 0xBD)
AMBER = (0xF4, 0xCB, 0x8E)
TOP, BOTTOM = (0x25, 0x2D, 0x36), (0x15, 0x1A, 0x20)
EDGE = (0x3A, 0x44, 0x50)


def render(size: int) -> Image.Image:
    small = size <= 24
    px = size * SUPER
    k = px / 128  # design unit -> pixels

    def s(v): return v * k

    # Background tile with a diagonal gradient, clipped to a rounded square.
    margin, radius = (1, 26) if small else (4, 28)
    gradient = Image.new("RGBA", (px, px))
    g = ImageDraw.Draw(gradient)
    for y in range(px):
        t = y / px
        g.line([(0, y), (px, y)], fill=tuple(round(a + (b - a) * t) for a, b in zip(TOP, BOTTOM)) + (255,))
    mask = Image.new("L", (px, px), 0)
    ImageDraw.Draw(mask).rounded_rectangle([s(margin), s(margin), s(128 - margin), s(128 - margin)], s(radius), fill=255)
    image = Image.new("RGBA", (px, px), (0, 0, 0, 0))
    image.paste(gradient, mask=mask)
    if not small:
        ImageDraw.Draw(image).rounded_rectangle(
            [s(margin), s(margin), s(128 - margin), s(128 - margin)], s(radius), outline=EDGE + (255,), width=max(1, round(s(1))))

    def layer(): return Image.new("RGBA", (px, px), (0, 0, 0, 0))

    def stroke_line(d, a, b, color, width):
        d.line([(s(a[0]), s(a[1])), (s(b[0]), s(b[1]))], fill=color, width=round(s(width)))
        for x, y in (a, b):
            r = s(width) / 2
            d.ellipse([s(x) - r, s(y) - r, s(x) + r, s(y) + r], fill=color)

    def stroke_arc(d, center, radius, start, end, color, width):
        cx, cy = center
        box = [s(cx - radius - width / 2), s(cy - radius - width / 2), s(cx + radius + width / 2), s(cy + radius + width / 2)]
        # Pillow draws the stroke inward from the box edge.
        d.arc(box, start, end, fill=color, width=round(s(width)))
        for angle in (start, end):
            x = cx + radius * math.cos(math.radians(angle))
            y = cy + radius * math.sin(math.radians(angle))
            r = s(width) / 2
            d.ellipse([s(x) - r, s(y) - r, s(x) + r, s(y) + r], fill=color)

    def composite(drawn, opacity):
        if opacity < 1:
            alpha = drawn.getchannel("A").point(lambda a: round(a * opacity))
            drawn.putalpha(alpha)
        image.alpha_composite(drawn)

    # Echo arcs reaching the microphone from the left, faded.
    if small:
        arcs = [((60, 64), 42, 0.7, 9)]
        slash = ((24, 100), (46, 28), 9)
        mic = dict(capsule=(62, 20, 96, 76), stand=((79, 60), 27), stem=((79, 89), (79, 104)), base=((62, 106), (96, 106)), width=10)
    else:
        arcs = [((49.6, 64), 28, 0.45, 6), ((49.4, 64), 42, 0.25, 6)]
        slash = ((16, 96), (44, 32), 6)
        mic = dict(capsule=(62, 26, 92, 76), stand=((77, 62), 23), stem=((77, 86), (77, 100)), base=((64, 102), (90, 102)), width=6)

    for center, radius, opacity, width in arcs:
        l = layer()
        stroke_arc(ImageDraw.Draw(l), center, radius, 134.4, 225.6, AMBER + (255,), width)
        composite(l, opacity)

    l = layer()
    d = ImageDraw.Draw(l)
    stroke_line(d, slash[0], slash[1], AMBER + (255,), slash[2])
    x0, y0, x1, y1 = mic["capsule"]
    d.rounded_rectangle([s(x0), s(y0), s(x1), s(y1)], s((x1 - x0) / 2), fill=ACCENT + (255,))
    (cx, cy), r = mic["stand"]
    stroke_arc(d, (cx, cy), r, 0, 180, ACCENT + (255,), mic["width"])
    stroke_line(d, *mic["stem"], ACCENT + (255,), mic["width"])
    stroke_line(d, *mic["base"], ACCENT + (255,), mic["width"])
    composite(l, 1)

    return image.resize((size, size), Image.LANCZOS)


def main():
    frames = {size: render(size) for size in SIZES}
    ICO.parent.mkdir(parents=True, exist_ok=True)
    frames[256].save(ICO, format="ICO", sizes=[(n, n) for n in SIZES], append_images=[frames[n] for n in SIZES if n != 256])

    # Preview strip on dark and light backgrounds (not shipped).
    width = sum(SIZES) + 12 * (len(SIZES) + 1)
    preview = Image.new("RGBA", (width, 2 * 256 + 36), (0x11, 0x14, 0x18, 255))
    ImageDraw.Draw(preview).rectangle([0, 256 + 18, width, 2 * 256 + 36], fill=(0xF3, 0xF4, 0xF6, 255))
    x = 12
    for size in SIZES:
        for top in (12 + 256 - size, 256 + 24 + 256 - size):
            preview.alpha_composite(frames[size], (x, top))
        x += size + 12
    preview.save(PREVIEW)
    print(f"wrote {ICO} and {PREVIEW}")


if __name__ == "__main__":
    main()
