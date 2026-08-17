"""Generate the WixUI bitmaps into installer/.

Outputs:
  dialog.bmp  493x312 - Welcome and Exit pages
  banner.bmp  493x58  - top banner on the remaining pages

The right-hand region is COLOR_BTNFACE grey on purpose. WixUI's ExitDialog declares its
optional checkbox without Transparent="yes", so the control paints the dialog's own background
colour; against WiX's stock bitmap, which is white there, that shows up as a grey band around
the "Launch VPNTray" row. The control lives in the UI extension's wixlib and cannot be patched
from outside, so the bitmap is the side that has to move.

The shield is lifted from the generated app icon rather than redrawn, so the installer shows
the same mark as the tray and the Start menu.

Run:  python scripts/make_installer_art.py
"""

from __future__ import annotations

import os

from PIL import Image, ImageDraw

HERE = os.path.dirname(__file__)
OUT_DIR = os.path.join(HERE, "..", "installer")
ICON = os.path.join(HERE, "..", "src", "VPNTray", "Resources", "icons", "app.ico")

DIALOG_SIZE = (493, 312)
BANNER_SIZE = (493, 58)
BAND_WIDTH = 164          # WixUI keeps its dialog text clear of roughly this much

BTN_FACE = (0xF0, 0xF0, 0xF0)   # default light-mode COLOR_BTNFACE
CREAM = (0xF4, 0xF1, 0xEA)      # the app's own window background


def shield(px: int) -> Image.Image:
    """The app icon at px, as RGBA."""
    with Image.open(ICON) as ico:
        ico.size = max(s for s in ico.info["sizes"] if s[0] >= px) if px <= 256 else (256, 256)
        return ico.convert("RGBA").resize((px, px), Image.LANCZOS)


def dialog() -> Image.Image:
    # No rule down the seam: at full dialog height an accent stripe stops reading as a detail and
    # starts reading as a wall splitting the page in two. The cream-to-grey change is edge enough.
    img = Image.new("RGB", DIALOG_SIZE, BTN_FACE)
    ImageDraw.Draw(img).rectangle([0, 0, BAND_WIDTH - 1, DIALOG_SIZE[1]], fill=CREAM)

    mark = shield(96)
    img.paste(mark, ((BAND_WIDTH - 96) // 2, 84), mark)
    return img


def banner() -> Image.Image:
    # Plain, like the dialog: the wizard already draws its own separator under this strip, so an
    # accent rule here only competes with it.
    img = Image.new("RGB", BANNER_SIZE, BTN_FACE)
    mark = shield(38)
    img.paste(mark, (BANNER_SIZE[0] - 38 - 14, (BANNER_SIZE[1] - 38) // 2), mark)
    return img


def main() -> None:
    for name, image in (("dialog.bmp", dialog()), ("banner.bmp", banner())):
        path = os.path.join(OUT_DIR, name)
        image.save(path, format="BMP")
        print(f"wrote {os.path.normpath(path)}  {image.size[0]}x{image.size[1]}")


if __name__ == "__main__":
    main()
