"""Generate the VPNTray icon set (shield + status dot) into src/VPNTray/Resources/icons/.

Outputs:
  app.ico          - plain shield (embedded in VPNTray.exe, used by installer shortcuts)
  connected.ico    - shield + green dot
  connecting.ico   - shield + orange dot
  disconnected.ico - shield + gray dot
  failed.ico       - shield + red dot

Run:  python scripts/make_icons.py
"""

from __future__ import annotations

import os

from PIL import Image, ImageDraw

OUT_DIR = os.path.join(os.path.dirname(__file__), "..", "src", "VPNTray", "Resources", "icons")
SS = 1024  # supersample canvas edge
ICO_SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]

SHIELD_TOP = (0x3B, 0x82, 0xF6)     # blue-500
SHIELD_BOTTOM = (0x1E, 0x40, 0xAF)  # blue-800
SHIELD_BORDER = (0x1E, 0x3A, 0x8A)  # blue-900
GLYPH = (255, 255, 255)

STATUS_COLORS = {
    "connected": (0x22, 0xC5, 0x5E),     # green
    "connecting": (0xF5, 0x9E, 0x0B),    # orange
    "disconnected": (0x9C, 0xA3, 0xAF),  # gray
    "failed": (0xEF, 0x44, 0x44),        # red
}


def quad_bezier(p0, p1, p2, steps=64):
    """Sample a quadratic bezier from p0 to p2 with control p1."""
    pts = []
    for i in range(steps + 1):
        t = i / steps
        x = (1 - t) ** 2 * p0[0] + 2 * (1 - t) * t * p1[0] + t ** 2 * p2[0]
        y = (1 - t) ** 2 * p0[1] + 2 * (1 - t) * t * p1[1] + t ** 2 * p2[1]
        pts.append((x, y))
    return pts


def shield_points(cx, cy, w, h):
    """Shield outline centered at (cx, cy) with total width w and height h, in pixels."""
    left, right = cx - w / 2, cx + w / 2
    top, tip = cy - h / 2, cy + h / 2
    # Gentle bow on the top edge, sides curving in to the bottom tip.
    pts = quad_bezier((left, top + 0.04 * h), (cx, top), (right, top + 0.04 * h), 32)
    pts += quad_bezier((right, top + 0.04 * h), (right, top + 0.62 * h), (cx, tip), 48)[1:]
    pts += quad_bezier((cx, tip), (left, top + 0.62 * h), (left, top + 0.04 * h), 48)[1:]
    return pts


def vertical_gradient(size, top_rgb, bottom_rgb):
    grad = Image.new("RGB", (1, size))
    for y in range(size):
        t = y / (size - 1)
        grad.putpixel((0, y), tuple(int(a + (b - a) * t) for a, b in zip(top_rgb, bottom_rgb)))
    return grad.resize((size, size))


def draw_shield(canvas):
    d = ImageDraw.Draw(canvas)
    cx, cy = SS * 0.5, SS * 0.52
    w, h = SS * 0.78, SS * 0.84

    # Border shield (slightly larger), then gradient fill clipped to the inner shield.
    d.polygon(shield_points(cx, cy, w, h), fill=SHIELD_BORDER)

    inner_mask = Image.new("L", (SS, SS), 0)
    ImageDraw.Draw(inner_mask).polygon(shield_points(cx, cy, w * 0.94, h * 0.94), fill=255)
    canvas.paste(vertical_gradient(SS, SHIELD_TOP, SHIELD_BOTTOM), (0, 0), inner_mask)

    # Keyhole glyph: circle + tapered stem.
    r = SS * 0.115
    kx, ky = cx, SS * 0.42
    d.ellipse([kx - r, ky - r, kx + r, ky + r], fill=GLYPH)
    stem_top, stem_bottom = SS * 0.055, SS * 0.085
    stem_end = SS * 0.66
    d.polygon(
        [(kx - stem_top, ky), (kx + stem_top, ky),
         (kx + stem_bottom, stem_end), (kx - stem_bottom, stem_end)],
        fill=GLYPH,
    )


def draw_status_dot(canvas, rgb):
    d = ImageDraw.Draw(canvas)
    cx, cy = SS * 0.74, SS * 0.76
    ring_r = SS * 0.235
    dot_r = SS * 0.185
    d.ellipse([cx - ring_r, cy - ring_r, cx + ring_r, cy + ring_r], fill=(255, 255, 255, 255))
    d.ellipse([cx - dot_r, cy - dot_r, cx + dot_r, cy + dot_r], fill=rgb + (255,))


def render(status_rgb=None):
    canvas = Image.new("RGBA", (SS, SS), (0, 0, 0, 0))
    draw_shield(canvas)
    if status_rgb is not None:
        draw_status_dot(canvas, status_rgb)
    return canvas


def save_ico(image, path):
    frames = [image.resize((s, s), Image.LANCZOS) for s in ICO_SIZES]
    frames[-1].save(
        path, format="ICO",
        sizes=[(s, s) for s in ICO_SIZES],
        append_images=frames[:-1],
    )
    print(f"wrote {os.path.normpath(path)}")


def main():
    os.makedirs(OUT_DIR, exist_ok=True)
    save_ico(render(), os.path.join(OUT_DIR, "app.ico"))
    for name, rgb in STATUS_COLORS.items():
        save_ico(render(rgb), os.path.join(OUT_DIR, f"{name}.ico"))

    # Side-by-side preview strip for eyeballing (not shipped).
    variants = [render()] + [render(rgb) for rgb in STATUS_COLORS.values()]
    cell = 128
    strip = Image.new("RGBA", (cell * len(variants), cell), (245, 245, 245, 255))
    for i, v in enumerate(variants):
        strip.paste(v.resize((cell, cell), Image.LANCZOS), (i * cell, 0))
    preview = os.path.join(os.path.dirname(__file__), "icons-preview.png")
    strip.save(preview)
    print(f"wrote {os.path.normpath(preview)}")


if __name__ == "__main__":
    main()
