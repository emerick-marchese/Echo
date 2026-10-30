"""Generates assets/listenteams.ico (waveform in a circle with a record dot)."""
from pathlib import Path
from PIL import Image, ImageDraw

SIZE = 1024
img = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
d = ImageDraw.Draw(img)

d.ellipse((24, 24, SIZE - 24, SIZE - 24), fill=(18, 94, 110, 255))

# Waveform bars
bars = [0.28, 0.5, 0.78, 0.5, 0.92, 0.6, 0.36]
bar_w = 74
gap = 34
total = len(bars) * bar_w + (len(bars) - 1) * gap
x = (SIZE - total) // 2
for h in bars:
    height = int(h * 520)
    top = SIZE // 2 - height // 2
    d.rounded_rectangle((x, top, x + bar_w, top + height), radius=bar_w // 2, fill=(255, 255, 255, 255))
    x += bar_w + gap

# Record dot
r = 150
cx, cy = SIZE - 230, SIZE - 230
d.ellipse((cx - r - 26, cy - r - 26, cx + r + 26, cy + r + 26), fill=(18, 94, 110, 255))
d.ellipse((cx - r, cy - r, cx + r, cy + r), fill=(232, 62, 62, 255))

out = Path(__file__).resolve().parent.parent / "assets" / "listenteams.ico"
img.save(out, sizes=[(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)])
print(out)
