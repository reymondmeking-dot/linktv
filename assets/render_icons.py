"""Render the LinkTV launcher mark and platform icon resources with Pillow."""

import io
import os
import struct
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter, ImageFont, ImageOps


ROOT = Path(__file__).resolve().parents[1]
SIDE = 1024
RESAMPLE = Image.Resampling.LANCZOS
INK = "#542E65"
CREAM = "#FFF5EC"
PINK = "#F698C1"
PURPLE = "#BC96E8"


def gradient(size):
    vertical = Image.linear_gradient("L").resize(size, RESAMPLE)
    horizontal = Image.linear_gradient("L").transpose(Image.Transpose.ROTATE_90).resize(size, RESAMPLE)
    return ImageOps.colorize(Image.blend(vertical, horizontal, 0.55), "#FFEFF2", "#DFC7FA")


def face(draw, x, y, separation):
    for eye in (x - separation, x + separation):
        draw.ellipse((eye - 17, y - 17, eye + 17, y + 17), fill=INK)
    draw.arc((x - 24, y - 1, x + 24, y + 32), 15, 165, fill=INK, width=12)
    for cheek in (x - separation - 29, x + separation + 29):
        draw.ellipse((cheek - 18, y + 16, cheek + 18, y + 38), fill="#F6ABC8")


def mascot_mark():
    art = Image.new("RGBA", (SIDE, SIDE))
    draw = ImageDraw.Draw(art)
    # The lavender television sits behind the pink laptop.
    draw.line(((592, 235), (535, 163)), fill=INK, width=24)
    draw.line(((609, 235), (665, 156)), fill=INK, width=24)
    draw.ellipse((511, 140, 554, 183), fill=CREAM, outline=INK, width=11)
    draw.ellipse((643, 132, 687, 176), fill=CREAM, outline=INK, width=11)
    draw.rounded_rectangle((402, 219, 855, 613), radius=96, fill=PURPLE, outline=INK, width=26)
    draw.rounded_rectangle((449, 267, 766, 553), radius=63, fill=CREAM, outline=INK, width=14)
    draw.ellipse((792, 366, 825, 399), fill=CREAM, outline=INK, width=7)
    draw.ellipse((792, 430, 825, 463), fill=CREAM, outline=INK, width=7)
    face(draw, 610, 390, 52)

    draw.rounded_rectangle((164, 389, 556, 710), radius=69, fill=PINK, outline=INK, width=26)
    draw.rounded_rectangle((208, 435, 512, 667), radius=43, fill=CREAM, outline=INK, width=14)
    face(draw, 357, 540, 53)
    draw.rounded_rectangle((125, 691, 599, 788), radius=45, fill="#F281B6", outline=INK, width=24)
    draw.rounded_rectangle((304, 714, 416, 744), radius=15, fill=CREAM)

    draw.ellipse((643, 661, 734, 751), fill="#DC5899")
    draw.ellipse((728, 661, 819, 751), fill="#DC5899")
    draw.polygon(((647, 729), (814, 729), (731, 811)), fill="#DC5899")
    return art


def icon_tile(mark, inset, radius):
    size = (SIDE, SIDE)
    value = gradient(size).convert("RGBA")
    shadow = Image.new("RGBA", size, "#8958AB")
    shifted = Image.new("L", size)
    shifted.paste(mark.getchannel("A"), (0, 19))
    shadow.putalpha(shifted.filter(ImageFilter.GaussianBlur(24)).point(lambda alpha: round(alpha * 0.22)))
    value.alpha_composite(shadow)
    value.alpha_composite(mark)
    if inset:
        mask = Image.new("L", size)
        ImageDraw.Draw(mask).rounded_rectangle((inset, inset, SIDE - inset - 1, SIDE - inset - 1), radius, fill=255)
        value.putalpha(mask)
    return value


def save(image, relative_path):
    path = ROOT / relative_path
    path.parent.mkdir(parents=True, exist_ok=True)
    image.save(path, optimize=True)


def app_icon_windows(tile):
    # Small entries are explicit so the Windows shell selects the nearest native size.
    sizes = (16, 20, 24, 32, 40, 48, 64, 128, 256)
    entries = []
    offset = 6 + 16 * len(sizes)
    header = io.BytesIO()
    header.write(struct.pack("<HHH", 0, 1, len(sizes)))
    for size in sizes:
        image = io.BytesIO()
        tile.resize((size, size), RESAMPLE).save(image, format="PNG", optimize=True)
        data = image.getvalue()
        dimension = size % 256
        header.write(struct.pack("<BBBBHHII", dimension, dimension, 0, 0, 1, 32, len(data), offset))
        entries.append(data)
        offset += len(data)
    (ROOT / "assets/linktv.ico").write_bytes(header.getvalue() + b"".join(entries))


def app_icon_android(tile, mark):
    layer = Image.new("RGBA", (SIDE, SIDE))
    cutout = mark.crop(mark.getbbox())
    # Keep the diagonal silhouette inside Android's circular launcher safe zone.
    cutout.thumbnail((480, 478), RESAMPLE)
    layer.alpha_composite(cutout, ((SIDE - cutout.width) // 2, (SIDE - cutout.height) // 2))
    for density, legacy_size, layer_size in (
        ("mdpi", 48, 108), ("hdpi", 72, 162), ("xhdpi", 96, 216),
        ("xxhdpi", 144, 324), ("xxxhdpi", 192, 432),
    ):
        folder = f"android/res/mipmap-{density}"
        save(tile.resize((legacy_size, legacy_size), RESAMPLE), f"{folder}/linktv_icon.png")
        save(layer.resize((layer_size, layer_size), RESAMPLE), f"{folder}/linktv_foreground.png")

    banner = gradient((1280, 720)).convert("RGBA")
    banner.alpha_composite(tile.resize((460, 460), RESAMPLE), (81, 130))
    font_path = Path(os.environ.get("WINDIR", r"C:\Windows")) / "Fonts/msyhbd.ttc"
    font = ImageFont.truetype(str(font_path), 116)
    ImageDraw.Draw(banner).text((574, 348), "双向投屏", font=font, fill=INK, anchor="lm")
    save(banner.convert("RGB").resize((320, 180), RESAMPLE), "android/res/drawable-xhdpi/linktv_banner.png")


def main():
    mark = mascot_mark()
    rounded = icon_tile(mark, 30, 217)
    mac = icon_tile(mark, 89, 184)
    opaque = icon_tile(mark, 0, 0).convert("RGB")
    apple = "apple/Resources/Assets.xcassets"
    save(opaque, "assets/linktv-icon.png")
    save(opaque, f"{apple}/AppIcon-iOS.appiconset/icon-1024.png")
    for size in (16, 32, 64, 128, 256, 512, 1024):
        save(mac.resize((size, size), RESAMPLE), f"{apple}/AppIcon-Mac.appiconset/icon-{size}.png")
    app_icon_windows(rounded)
    app_icon_android(rounded, mark)


if __name__ == "__main__":
    main()
