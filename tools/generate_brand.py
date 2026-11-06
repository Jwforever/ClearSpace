"""Render the hand-drawn vector mark and Windows ICO. Requires Pillow."""

from pathlib import Path
from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[1]
SCALE = 8
GREEN = "#B9DEAF"
INK = "#163F30"


def cubic(start, control_a, control_b, end, steps=100):
    points = []
    for i in range(steps + 1):
        t = i / steps
        u = 1 - t
        points.append(tuple(
            SCALE * (u**3 * start[n] + 3 * u**2 * t * control_a[n]
                     + 3 * u * t**2 * control_b[n] + t**3 * end[n])
            for n in (0, 1)
        ))
    return points


def rounded_line(draw, points, width, color):
    draw.line(points, fill=color, width=round(width * SCALE))
    radius = width * SCALE / 2
    for x, y in points:
        draw.ellipse((x - radius, y - radius, x + radius, y + radius), fill=color)


def render_mark():
    image = Image.new("RGBA", (256 * SCALE, 256 * SCALE))
    draw = ImageDraw.Draw(image)
    draw.rounded_rectangle(tuple(v * SCALE for v in (8, 8, 248, 248)),
                           radius=60 * SCALE, fill=GREEN)
    leaf = cubic((65, 182), (48, 120), (102, 62), (194, 57))
    leaf += cubic((194, 57), (194, 140), (150, 196), (82, 194))[1:]
    leaf += cubic((82, 194), (75, 193), (68, 189), (65, 182))[1:]
    rounded_line(draw, leaf, 14, INK)
    rounded_line(draw, [(63 * SCALE, 199 * SCALE), (161 * SCALE, 103 * SCALE)], 13, INK)
    draw.polygon([(x * SCALE, y * SCALE) for x, y in
                  [(60, 53), (65, 65), (77, 70), (65, 75),
                   (60, 87), (55, 75), (43, 70), (55, 65)]], fill="#247C62")
    return image


def main():
    assets = ROOT / "assets"
    docs = ROOT / "docs"
    app_assets = ROOT / "src" / "ClearSpace.App" / "assets"
    for folder in (assets, docs, app_assets):
        folder.mkdir(parents=True, exist_ok=True)
    image = render_mark()
    mark = image.resize((512, 512), Image.Resampling.LANCZOS)
    mark.save(assets / "clearspace-mark.png")
    mark.save(assets / "clearspace.png")
    mark.save(app_assets / "clearspace-mark.png")
    mark.save(assets / "ClearSpace.ico", sizes=[(n, n) for n in (16, 24, 32, 40, 48, 64, 96, 128, 256)])
    image.resize((256, 256), Image.Resampling.LANCZOS).save(docs / "icon-preview.png")

    banner = Image.new("RGB", (1600, 620), "#F5F7F1")
    draw = ImageDraw.Draw(banner)
    draw.rounded_rectangle((28, 28, 1572, 592), radius=38, fill="#EBF2E4")
    draw.ellipse((1260, -210, 1810, 340), fill="#DCEBD3")
    draw.ellipse((-240, 450, 300, 990), fill="#E0ECD7")
    banner_mark = image.resize((300, 300), Image.Resampling.LANCZOS)
    banner.paste(banner_mark, (110, 145), banner_mark)
    fonts = Path("C:/Windows/Fonts")
    title_font = ImageFont.truetype(str(fonts / "msyhbd.ttc"), 86)
    latin_font = ImageFont.truetype(str(fonts / "segoeui.ttf"), 57)
    body_font = ImageFont.truetype(str(fonts / "msyh.ttc"), 30)
    small_font = ImageFont.truetype(str(fonts / "segoeui.ttf"), 23)
    draw.text((480, 135), "清屿", font=title_font, fill=INK)
    draw.text((705, 167), "ClearSpace", font=latin_font, fill=INK)
    draw.text((486, 270), "让电脑，轻一点。", font=body_font, fill="#597557")
    draw.rounded_rectangle((486, 357, 1180, 412), radius=16, fill="#DCEACF")
    draw.text((510, 365), "软件管理  /  空间清理  /  本地运行", font=body_font, fill="#456344")
    draw.text((486, 459), "WINDOWS 10 / 11   ·   PORTABLE   ·   NO ACCOUNT", font=small_font, fill="#71866D")
    banner.save(docs / "clearspace-banner.png", optimize=True)


if __name__ == "__main__":
    main()
