"""Generates CCNest's application icon (multi-resolution .ico + PNG) with Pillow.

Design: a warm terracotta rounded square with a stylised white "nest" — two nested arcs
cradling a dot — symbolising a protected, self-contained environment.
"""
import sys
from PIL import Image, ImageDraw

def render(size: int) -> Image.Image:
    s = 4  # supersample
    W = size * s
    img = Image.new("RGBA", (W, W), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    r = int(W * 0.22)
    # vertical gradient terracotta background
    grad = Image.new("RGBA", (W, W))
    gd = ImageDraw.Draw(grad)
    top, bottom = (222, 125, 90), (186, 86, 55)
    for y in range(W):
        t = y / (W - 1)
        gd.line([(0, y), (W, y)], fill=tuple(int(top[i] + (bottom[i] - top[i]) * t) for i in range(3)) + (255,))
    mask = Image.new("L", (W, W), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, W - 1, W - 1], radius=r, fill=255)
    img.paste(grad, (0, 0), mask)
    white = (255, 255, 255, 255)
    stroke = max(2, int(W * 0.075))
    # outer nest arc
    m = int(W * 0.18)
    d.arc([m, int(W * 0.10), W - m, W - int(W * 0.18)], start=20, end=160, fill=white, width=stroke)
    # inner nest arc
    m2 = int(W * 0.30)
    d.arc([m2, int(W * 0.24), W - m2, W - int(W * 0.30)], start=25, end=155, fill=white, width=stroke)
    # the protected "egg"
    cx, cy, rr = W // 2, int(W * 0.47), int(W * 0.095)
    d.ellipse([cx - rr, cy - rr, cx + rr, cy + rr], fill=white)
    return img.resize((size, size), Image.LANCZOS)

def main(out_dir: str) -> None:
    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    images = [render(sz) for sz in sizes]
    images[-1].save(f"{out_dir}/ccnest.ico", sizes=[(sz, sz) for sz in sizes], append_images=images[:-1])
    render(256).save(f"{out_dir}/ccnest-256.png")
    render(64).save(f"{out_dir}/ccnest-64.png")

if __name__ == "__main__":
    main(sys.argv[1])
