"""Generates assets/echo.ico and assets/echo.png: a sound source with echo waves on a gradient tile."""
from pathlib import Path
from PIL import Image, ImageDraw

SIZE = 1024
ROOT = Path(__file__).resolve().parent.parent


def gradient(size, start, end):
    img = Image.new("RGBA", (size, size))
    px = img.load()
    for y in range(size):
        for x in range(size):
            t = (x + y) / (2 * (size - 1))
            px[x, y] = tuple(int(start[i] + (end[i] - start[i]) * t) for i in range(3)) + (255,)
    return img


def build():
    tile = gradient(SIZE, (124, 92, 255), (34, 200, 238))
    mask = Image.new("L", (SIZE, SIZE), 0)
    ImageDraw.Draw(mask).rounded_rectangle((40, 40, SIZE - 40, SIZE - 40), radius=230, fill=255)
    img = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    img.paste(tile, (0, 0), mask)

    d = ImageDraw.Draw(img)
    cx, cy = 380, SIZE // 2
    white = (255, 255, 255, 255)
    d.ellipse((cx - 85, cy - 85, cx + 85, cy + 85), fill=white)
    # Echo waves, fading outwards.
    for i, (radius, alpha) in enumerate([(220, 255), (350, 200), (480, 130)]):
        layer = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
        ImageDraw.Draw(layer).arc(
            (cx - radius, cy - radius, cx + radius, cy + radius),
            start=-48, end=48, fill=(255, 255, 255, alpha), width=62 - i * 6)
        img = Image.alpha_composite(img, layer)
    clipped = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    clipped.paste(img, (0, 0), mask)
    return clipped


icon = build()
assets = ROOT / "assets"
icon.save(assets / "echo.ico", sizes=[(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)])
icon.resize((256, 256), Image.LANCZOS).save(assets / "echo.png")
print(assets / "echo.ico")
