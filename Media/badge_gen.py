#!/usr/bin/env python3
"""LQ badge + Workshop preview. Same geometry system as the CE+SS suite badges
(300x100 bar/circle/ring-knockout; 512 preview) - visually consistent, distinct
identity: masterwork-teal accent (quality), emblem = CE's rifle glyph (this is a CE
mod; glyph remixed from CE's Badge_CE_compatible.svg, CC BY-NC-SA, CE team)
crowned with a quality star. Run from Media/: python3 badge_gen.py"""
import collections
import io
import os
from PIL import Image, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
FONT = "/usr/share/fonts/dejavu-sans-fonts/DejaVuSansCondensed-Bold.ttf"
S = 4
BLACK = (0, 0, 0, 255)
WHITE = (255, 255, 255, 255)
GOLD = (0, 168, 156, 255)  # masterwork teal - amber/gold collided with the Loadouts Module badge


def extract_rifle():
    import cairosvg
    buf = io.BytesIO()
    cairosvg.svg2png(url=os.path.join(HERE, "Badge_CE_compatible.svg"), write_to=buf, scale=8)
    buf.seek(0)
    src = Image.open(buf).convert("RGBA")
    Z = 8
    px = src.load()
    pts = [(x, y) for x in range(105 * Z) for y in range(100 * Z)
           if px[x, y][0] > 200 and px[x, y][3] > 200]
    ptset = set(pts)
    seen = set()
    clusters = []
    for p in pts:
        if p in seen:
            continue
        q = collections.deque([p])
        comp = []
        seen.add(p)
        while q:
            c = q.popleft()
            comp.append(c)
            x, y = c
            for dx in (-1, 0, 1):
                for dy in (-1, 0, 1):
                    n = (x + dx, y + dy)
                    if n in ptset and n not in seen:
                        seen.add(n)
                        q.append(n)
        clusters.append(comp)

    def is_rifle(comp):
        xs = [p[0] for p in comp]
        ys = [p[1] for p in comp]
        return (min(xs) + max(xs)) / 2 < 50 * Z and (min(ys) + max(ys)) / 2 > 40 * Z

    comp = max((c for c in clusters if is_rifle(c)), key=len)
    xs = [p[0] for p in comp]
    ys = [p[1] for p in comp]
    x0, y0 = min(xs), min(ys)
    m = Image.new("L", (max(xs) - x0 + 1, max(ys) - y0 + 1), 0)
    mp = m.load()
    for x, y in comp:
        mp[x - x0, y - y0] = 255
    m = m.rotate(45, expand=True, resample=Image.BICUBIC)
    m = m.transpose(Image.FLIP_LEFT_RIGHT).point(lambda v: 255 if v > 110 else 0)
    return m.crop(m.getbbox())


def star(d, cx, cy, r, fill):
    import math
    pts = []
    for i in range(10):
        ang = -math.pi / 2 + i * math.pi / 5
        rad = r if i % 2 == 0 else r * 0.42
        pts.append((cx + rad * math.cos(ang), cy + rad * math.sin(ang)))
    d.polygon(pts, fill=fill)


def emblem(img, d, cx_units, rifle, scale):
    """Rifle with a quality star above, centered on circle center."""
    def paste_glyph(m, gx, gy, target_w):
        sc = target_w / m.width
        g = m.resize((int(m.width * sc), int(m.height * sc)), Image.LANCZOS)
        img.paste(WHITE, (int(gx - g.width / 2), int(gy - g.height / 2)), g)
    paste_glyph(rifle, cx_units[0], cx_units[1] + 8 * scale, 62 * scale)
    star(d, cx_units[0], cx_units[1] - 26 * scale, 14 * scale, GOLD)


def draw_row(d, text, font, cx, y, target_w, fill):
    """Draw text tracked (letter-spaced) to span target_w, centered on cx."""
    natural = d.textlength(text, font=font)
    gap = (target_w - natural) / (len(text) - 1) if len(text) > 1 else 0
    x = cx - target_w / 2
    for ch in text:
        d.text((x, y), ch, font=font, fill=fill)
        x += d.textlength(ch, font=font) + gap


def render_badge(rifle):
    W, H = 300 * S, 100 * S
    bar = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    db = ImageDraw.Draw(bar)
    db.rectangle([0, 25 * S, 300 * S, 74 * S], fill=BLACK)
    hole = Image.new("L", (W, H), 0)
    dh = ImageDraw.Draw(hole)
    cx, cy, r, gap = 50 * S, 50 * S, 50 * S, 5 * S
    dh.ellipse([cx - (r + gap), cy - (r + gap), cx + (r + gap), cy + (r + gap)], fill=255)
    dh.rectangle([0, 0, 5 * S, H], fill=255)
    bar.putalpha(Image.composite(Image.new("L", (W, H), 0), bar.getchannel("A"), hole))

    img = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    img.alpha_composite(bar)
    d = ImageDraw.Draw(img)
    d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=BLACK)
    d.ellipse([cx - r, cy - r, cx + r, cy + r], outline=GOLD, width=3 * S)
    emblem(img, d, (cx, cy), rifle, S)

    def fit_rows(texts, max_size, max_w):
        s = int(max_size)
        while s > 6 and max(d.textlength(t, font=ImageFont.truetype(FONT, s)) for t in texts) > max_w:
            s -= 1
        return ImageFont.truetype(FONT, s)
    CX = 202 * S
    row1 = "PAWNS OPTIMIZE"
    row2a, row2b = "WEAPON ", "QUALITY"
    row2 = row2a + row2b
    tf = fit_rows([row1, row2], 15 * S, 190 * S)
    lh = sum(tf.getmetrics())
    y0 = 50 * S - lh  # two equal rows straddling the bar mid (~50*S)
    target = max(d.textlength(row1, font=tf), d.textlength(row2, font=tf))
    draw_row(d, row1, tf, CX, y0, target, WHITE)
    draw_row(d, row2, tf, CX, y0 + lh, target, GOLD)
    img.resize((300, 100), Image.LANCZOS).save(os.path.join(HERE, "Badge_POWQ.png"))
    print("wrote Badge_POWQ.png")


def render_preview(rifle):
    P = 4
    W = H = 512 * P
    img = Image.new("RGBA", (W, H), (12, 12, 12, 255))
    d = ImageDraw.Draw(img)
    cx, cy, r = 256 * P, 190 * P, 140 * P
    d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=BLACK, outline=GOLD, width=8 * P)

    def paste_glyph(m, gx, gy, target_w):
        sc = target_w / m.width
        g = m.resize((int(m.width * sc), int(m.height * sc)), Image.LANCZOS)
        img.paste(WHITE, (int(gx - g.width / 2), int(gy - g.height / 2)), g)

    paste_glyph(rifle, cx, (190 + 25) * P, 176 * P)
    star(d, cx, (190 - 75) * P, 40 * P, GOLD)

    def fitp(texts, max_size, max_w):
        s = int(max_size)
        while s > 10 and max(d.textlength(t, font=ImageFont.truetype(FONT, s)) for t in texts) > max_w:
            s -= 1
        return ImageFont.truetype(FONT, s)
    prow1, prow2 = "PAWNS OPTIMIZE", "WEAPON QUALITY"
    ftitle = fitp([prow1, prow2], 42 * P, 470 * P)
    plh = sum(ftitle.getmetrics())
    ytop = 367 * P
    for text, y, color in [(prow1, ytop, WHITE), (prow2, ytop + plh, GOLD)]:
        w = d.textlength(text, font=ftitle)
        d.text(((W - w) / 2, y), text, font=ftitle, fill=color)
    img.resize((512, 512), Image.LANCZOS).save(os.path.join(HERE, "..", "About", "Preview.png"))
    print("wrote About/Preview.png")


if __name__ == "__main__":
    rifle = extract_rifle()
    render_badge(rifle)
    render_preview(rifle)
