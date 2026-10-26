"""Draw the Hall Monitor NPC sprites and poster procedurally (no game artwork used).

Run: uv run --with pillow --with numpy python scripts/draw-hall-monitor.py
Writes assets/npc/hall-monitor-{patrol,writing,whistle}.png (256x512) and hall-monitor-poster.png (256x256).
The style imitates the game's shaded, rounded pseudo-3D characters: every part is a lit sphere or tube."""
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont

OUT = Path(__file__).resolve().parents[1] / "assets" / "npc"
W, H = 512, 1024  # working canvas, downsampled 2x
LIGHT = np.array([-0.45, -0.55, 0.70])
LIGHT = LIGHT / np.linalg.norm(LIGHT)

SKIN = (236, 196, 150)
SHIRT = (120, 170, 222)
PANTS = (52, 62, 110)
SHOE = (70, 44, 30)
CAP = (32, 78, 170)
SASH = (255, 150, 30)
STRIPE = (255, 240, 120)
HAIR = (120, 72, 36)


class Canvas:
    def __init__(self, w=W, h=H):
        self.rgb = np.zeros((h, w, 3), np.float32)
        self.a = np.zeros((h, w), np.float32)
        self.yy, self.xx = np.mgrid[0:h, 0:w].astype(np.float32)

    def paint(self, mask, color):
        """mask: float alpha 0..1, color: HxWx3 or rgb tuple."""
        col = np.broadcast_to(np.asarray(color, np.float32), self.rgb.shape)
        m = mask[..., None]
        self.rgb = self.rgb * (1 - m) + col * m
        self.a = self.a + mask * (1 - self.a)

    def shaded(self, nx, ny, inside, base, ambient=0.42, gain=0.72):
        nz = np.sqrt(np.clip(1 - nx * nx - ny * ny, 0, 1))
        lam = np.clip(nx * LIGHT[0] + ny * LIGHT[1] + nz * LIGHT[2], 0, 1)
        spec = np.clip(lam, 0, 1) ** 24 * 0.35
        shade = ambient + gain * lam
        col = np.asarray(base, np.float32)[None, None, :] * shade[..., None] + 255 * spec[..., None]
        edge = np.clip((1 - np.sqrt(np.clip(nx * nx + ny * ny, 0, 1))) * 40, 0, 1)  # soft anti-aliased rim
        self.paint(inside * edge, np.clip(col, 0, 255))

    def ellipse(self, cx, cy, rx, ry, base, **kw):
        nx = (self.xx - cx) / rx
        ny = (self.yy - cy) / ry
        inside = (nx * nx + ny * ny <= 1).astype(np.float32)
        self.shaded(nx, ny, inside, base, **kw)

    def tube(self, x0, y0, x1, y1, r, base, r1=None, **kw):
        r1 = r if r1 is None else r1
        d = np.array([x1 - x0, y1 - y0], np.float32)
        length2 = float(d @ d)
        t = np.clip(((self.xx - x0) * d[0] + (self.yy - y0) * d[1]) / length2, 0, 1)
        px, py = x0 + t * d[0], y0 + t * d[1]
        rad = r + (r1 - r) * t
        ox, oy = (self.xx - px) / rad, (self.yy - py) / rad
        inside = (ox * ox + oy * oy <= 1).astype(np.float32)
        self.shaded(ox, oy, inside, base, **kw)

    def flat(self, draw_fn, color, blur=0.8):
        """Flat details drawn with PIL (lines, text, polygons), lightly anti-aliased."""
        layer = Image.new("L", (self.a.shape[1], self.a.shape[0]), 0)
        draw_fn(ImageDraw.Draw(layer))
        if blur:
            layer = layer.filter(ImageFilter.GaussianBlur(blur))
        self.paint(np.asarray(layer, np.float32) / 255, color)

    def image(self, size):
        rgba = np.dstack([np.clip(self.rgb, 0, 255), np.clip(self.a * 255, 0, 255)]).astype(np.uint8)
        return Image.fromarray(rgba, "RGBA").resize(size, Image.LANCZOS)


def font(size, bold=True):
    for name in (("arialbd.ttf" if bold else "arial.ttf"), "DejaVuSans-Bold.ttf"):
        try:
            return ImageFont.truetype(name, size)
        except OSError:
            pass
    return ImageFont.load_default()


def body(c, pose):
    # Legs and shoes.
    for x in (218, 294):
        c.tube(x, 650, x, 950, 27, PANTS)
        c.ellipse(x + (6 if x > 256 else -6), 962, 42, 24, SHOE)
    # Torso: a tall rounded body like the game's characters.
    c.ellipse(256, 545, 104, 150, SHIRT)
    # Collar.
    c.flat(lambda d: d.polygon([(214, 412), (256, 452), (298, 412), (256, 432)], fill=255), (240, 240, 245))
    # Safety sash, shoulder to hip, with a reflective stripe.
    c.flat(lambda d: d.line([(178, 430), (330, 680)], fill=255, width=46), SASH)
    c.flat(lambda d: d.line([(178, 430), (330, 680)], fill=255, width=10), STRIPE)
    # Badge on the chest.
    star = [(310, 470), (318, 490), (340, 492), (323, 506), (329, 528), (310, 515), (291, 528), (297, 506), (280, 492), (302, 490)]
    c.flat(lambda d: d.polygon(star, fill=255), (240, 200, 40), blur=0.6)
    c.flat(lambda d: d.line(star + [star[0]], fill=255, width=3), (150, 110, 20), blur=0.4)
    # Lanyard and whistle (hanging unless he is blowing it).
    if pose != "whistle":
        c.flat(lambda d: d.line([(222, 420), (256, 560), (290, 420)], fill=255, width=6), (210, 30, 40))
        c.tube(246, 572, 272, 572, 11, (205, 210, 220), ambient=0.5)
        c.ellipse(244, 578, 15, 15, (205, 210, 220), ambient=0.5)


def arms(c, pose):
    # His left arm (viewer's right) holds the clipboard across the belly.
    c.tube(350, 430, 372, 560, 25, SHIRT)
    c.tube(372, 560, 290, 610, 23, SHIRT)
    # Clipboard.
    c.flat(lambda d: d.rounded_rectangle([170, 560, 300, 720], radius=8, fill=255), (150, 98, 52), blur=0.6)
    c.flat(lambda d: d.rectangle([182, 580, 288, 708], fill=255), (250, 250, 244), blur=0.4)
    for y in range(600, 700, 16):
        c.flat(lambda d, y=y: d.line([(192, y), (278, y)], fill=255, width=3), (120, 150, 210), blur=0.3)
    c.flat(lambda d: d.rounded_rectangle([212, 552, 258, 574], radius=5, fill=255), (190, 192, 200), blur=0.4)
    c.ellipse(292, 612, 22, 20, SKIN)  # hand on the clipboard edge
    if pose == "writing":
        # Marks on the sheet and a pencil in his right hand.
        c.flat(lambda d: d.line([(196, 620), (214, 640), (244, 600)], fill=255, width=6), (200, 30, 30))
        c.tube(160, 430, 150, 560, 25, SHIRT)
        c.tube(150, 560, 228, 650, 23, SHIRT)
        c.flat(lambda d: d.line([(228, 650), (262, 610)], fill=255, width=10), (250, 200, 40), blur=0.5)
        c.flat(lambda d: d.line([(262, 610), (270, 600)], fill=255, width=6), (60, 40, 30), blur=0.4)
        c.ellipse(230, 652, 22, 20, SKIN)
    elif pose == "whistle":
        # Right hand raised to the whistle in his mouth.
        c.tube(160, 430, 132, 330, 25, SHIRT)
        c.tube(132, 330, 222, 330, 22, SHIRT)
        c.ellipse(226, 332, 22, 20, SKIN)
    else:
        c.tube(160, 430, 140, 580, 25, SHIRT)
        c.tube(140, 580, 148, 660, 22, SHIRT)
        c.ellipse(150, 672, 22, 22, SKIN)


def head(c, pose):
    cx, cy = 256, 262
    c.ellipse(cx - 116, cy + 6, 22, 32, SKIN)
    c.ellipse(cx + 116, cy + 6, 22, 32, SKIN)
    c.ellipse(cx, cy, 120, 124, SKIN)
    # Short hair under the cap at the sides.
    c.flat(lambda d: d.chord([cx - 122, cy - 120, cx - 60, cy + 10], 120, 260, fill=255), HAIR)
    c.flat(lambda d: d.chord([cx + 60, cy - 120, cx + 122, cy + 10], 280, 60, fill=255), HAIR)
    # Cap: dome and a brim facing forward, with an HM patch.
    c.ellipse(cx, cy - 92, 116, 64, CAP)
    c.flat(lambda d: d.rectangle([cx - 118, cy - 96, cx + 118, cy - 70], fill=255), CAP, blur=0.6)
    c.ellipse(cx + 6, cy - 66, 128, 22, tuple(int(v * 0.8) for v in CAP), ambient=0.55)
    c.flat(lambda d: d.ellipse([cx - 34, cy - 138, cx + 34, cy - 92], fill=255), (250, 250, 250), blur=0.5)
    c.flat(lambda d: d.text((cx, cy - 115), "HM", fill=255, font=font(30), anchor="mm"), (200, 30, 30), blur=0)
    # Eyes behind square glasses.
    look = {"patrol": (0, 0), "writing": (0, 12), "whistle": (0, 0)}[pose]
    for ex in (cx - 46, cx + 46):
        if pose == "whistle":
            c.flat(lambda d, ex=ex: d.arc([ex - 26, cy - 18, ex + 26, cy + 18], 200, 340, fill=255, width=7), (30, 20, 20))
        else:
            c.ellipse(ex, cy - 2, 26, 30, (250, 250, 250), ambient=0.75, gain=0.3)
            c.flat(lambda d, ex=ex: d.ellipse([ex - 11 + look[0], cy - 12 + look[1], ex + 11 + look[0], cy + 10 + look[1]], fill=255), (20, 20, 30), blur=0.5)
        c.flat(lambda d, ex=ex: d.rounded_rectangle([ex - 38, cy - 36, ex + 38, cy + 32], radius=10, outline=255, width=7), (30, 30, 40), blur=0.5)
    c.flat(lambda d: d.line([(cx - 8, cy - 8), (cx + 8, cy - 8)], fill=255, width=7), (30, 30, 40))
    # Stern brows slanting down to the middle.
    c.flat(lambda d: d.line([(cx - 86, cy - 52), (cx - 18, cy - 38)], fill=255, width=10), HAIR)
    c.flat(lambda d: d.line([(cx + 86, cy - 52), (cx + 18, cy - 38)], fill=255, width=10), HAIR)
    # Nose.
    c.ellipse(cx, cy + 34, 16, 14, SKIN, ambient=0.5)
    if pose == "whistle":
        # Puffed red cheeks and a whistle in his mouth, with blast lines.
        c.ellipse(cx - 70, cy + 62, 34, 28, (240, 140, 120))
        c.ellipse(cx + 70, cy + 62, 34, 28, (240, 140, 120))
        c.tube(cx - 8, cy + 76, cx + 50, cy + 76, 13, (205, 210, 220), ambient=0.5)
        c.ellipse(cx - 12, cy + 80, 18, 18, (205, 210, 220), ambient=0.5)
        for dy in (-30, 0, 30):
            c.flat(lambda d, dy=dy: d.line([(cx + 140, cy + 76 + dy // 2), (cx + 190, cy + 76 + dy)], fill=255, width=8), (40, 40, 40))
    elif pose == "writing":
        c.flat(lambda d: d.arc([cx - 30, cy + 48, cx + 30, cy + 84], 20, 160, fill=255, width=7), (120, 40, 40))
    else:
        c.flat(lambda d: d.line([(cx - 34, cy + 74), (cx + 34, cy + 70)], fill=255, width=8), (120, 40, 40))


def frame(pose):
    c = Canvas()
    body(c, pose)
    arms(c, pose)
    head(c, pose)
    return c.image((256, 512))


def poster(portrait):
    """Base image in the game's character-poster layout (measured from the native posters, not copied):
    transparent 256x256, a framed portrait at x14-113 y30-161, an empty white card at x142-241 y30-161 and a
    name plate at x48-207 y176-207, all tan (219,155,85). The game writes the name and description into the
    card and plate at runtime with its own font, from the poster's text data (HallMonitor.cs)."""
    tan = (219, 155, 85, 255)
    img = Image.new("RGBA", (256, 256), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.rectangle([14, 30, 113, 161], fill=tan)
    d.rectangle([16, 32, 111, 159], fill=(0, 0, 0, 255))
    d.rectangle([142, 30, 241, 161], fill=tan)
    d.rectangle([144, 32, 239, 159], fill=(255, 255, 255, 255))
    d.rectangle([48, 176, 207, 207], fill=tan)
    # Head and shoulders, then reduced to a small dithered palette like the game's poster portraits.
    bust = portrait.crop((28, 30, 228, 297)).resize((96, 128), Image.LANCZOS)
    back = Image.new("RGBA", bust.size, (0, 0, 0, 255)); back.alpha_composite(bust)
    small = back.convert("RGB").quantize(colors=48, dither=Image.FLOYDSTEINBERG).convert("RGBA")
    img.paste(small, (16, 32))
    return img


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    frames = {}
    for pose in ("patrol", "writing", "whistle"):
        frames[pose] = frame(pose)
        frames[pose].save(OUT / ("hall-monitor-" + pose + ".png"))
    poster(frames["patrol"]).save(OUT / "hall-monitor-poster.png")
    sheet = Image.new("RGBA", (256 * 3 + 256, 512), (40, 40, 48, 255))
    for i, pose in enumerate(("patrol", "writing", "whistle")):
        sheet.alpha_composite(frames[pose], (256 * i, 0))
    sheet.alpha_composite(poster(frames["patrol"]), (768, 128))
    sheet.save(OUT.parent.parent / "private" / "hall-monitor-preview.png")
    print("wrote", sorted(p.name for p in OUT.glob("hall-monitor-*.png")))


if __name__ == "__main__":
    main()
