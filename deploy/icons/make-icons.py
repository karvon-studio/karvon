#!/usr/bin/env python3
"""Karvon Studio's logo and every icon made from it: a caravan of two-humped camels on a dune's crest at sunset.
Drawn in code, so it stays one source: needs pycairo and Pillow. Run from anywhere: deploy/icons/make-icons.py

Large sizes show the three camels. Small ones (64 pixels and under) show one, as three would be lost there.
Writes src/Karvon.Core/Assets/karvon.{png,svg} and karvon-small.png, deploy/windows/karvon.ico and, here, the sizes the
Linux and macOS packages take.
"""
import math
from pathlib import Path

import cairo
from PIL import Image

ROOT = Path(__file__).resolve().parents[2]
ASSETS = ROOT / "src/Karvon.Core/Assets"
HERE = Path(__file__).resolve().parent
SMALL_SIZES = (16, 24, 32, 48, 64)


def hexc(h, a=1.0):
    h = h.lstrip("#")
    return (int(h[0:2], 16) / 255, int(h[2:4], 16) / 255, int(h[4:6], 16) / 255, a)


def spline(points, steps=24, closed=False):
    """A Catmull-Rom curve through the points."""
    n = len(points)
    out = []
    for i in range(n if closed else n - 1):
        if closed:
            p0, p1, p2, p3 = points[(i - 1) % n], points[i], points[(i + 1) % n], points[(i + 2) % n]
        else:
            p0, p1, p2, p3 = points[max(i - 1, 0)], points[i], points[i + 1], points[min(i + 2, n - 1)]
        for k in range(steps):
            t = k / steps
            out.append(tuple(
                0.5 * (2 * p1[j] + (p2[j] - p0[j]) * t + (2 * p0[j] - 5 * p1[j] + 4 * p2[j] - p3[j]) * t * t + (3 * p1[j] - p0[j] - 3 * p2[j] + p3[j]) * t * t * t)
                for j in (0, 1)))
    if not closed:
        out.append(points[-1])
    return out


def resample(line, count):
    """The line as count points evenly spaced along it."""
    lengths = [0.0]
    for a, b in zip(line, line[1:]):
        lengths.append(lengths[-1] + math.dist(a, b))
    out, j = [], 0
    for i in range(count):
        s = lengths[-1] * i / (count - 1)
        while j < len(lengths) - 2 and lengths[j + 1] < s:
            j += 1
        t = (s - lengths[j]) / (lengths[j + 1] - lengths[j] or 1.0)
        out.append((line[j][0] + (line[j + 1][0] - line[j][0]) * t, line[j][1] + (line[j + 1][1] - line[j][1]) * t))
    return out


def path(ctx, points, close=True):
    ctx.move_to(*points[0])
    for p in points[1:]:
        ctx.line_to(*p)
    if close:
        ctx.close_path()


def fill(ctx, points, color):
    path(ctx, points)
    ctx.set_source_rgba(*color)
    ctx.fill()


def stroke(ctx, points, color, width):
    path(ctx, points, close=False)
    ctx.set_source_rgba(*color)
    ctx.set_line_width(width)
    ctx.set_line_join(cairo.LINE_JOIN_ROUND)
    ctx.set_line_cap(cairo.LINE_CAP_ROUND)
    ctx.stroke()


def swell(ctx, line, widths, color):
    """An engraver's line: a width at each point of the centre line."""
    n = len(line)
    left, right = [], []
    for i, w in enumerate(widths):
        a, b = line[max(i - 1, 0)], line[min(i + 1, n - 1)]
        dx, dy = b[0] - a[0], b[1] - a[1]
        d = math.hypot(dx, dy) or 1.0
        left.append((line[i][0] - dy / d * w / 2, line[i][1] + dx / d * w / 2))
        right.append((line[i][0] + dy / d * w / 2, line[i][1] - dx / d * w / 2))
    fill(ctx, left + right[::-1], color)


def limb(points, widths, steps=14):
    """A shape around a centre line, its width interpolated: a leg, a tail."""
    line = spline(points, steps)
    ws = spline([(i, w) for i, w in enumerate(widths)], steps)
    left, right = [], []
    for i, p in enumerate(line):
        a, b = line[max(i - 1, 0)], line[min(i + 1, len(line) - 1)]
        dx, dy = b[0] - a[0], b[1] - a[1]
        d = math.hypot(dx, dy) or 1.0
        w = ws[i][1] / 2
        left.append((p[0] - dy / d * w, p[1] + dx / d * w))
        right.append((p[0] + dy / d * w, p[1] - dx / d * w))
    return left + right[::-1]


# ---- The camel, in its own coordinates ----------------------------------------------------------------------------------

BODY = spline([
    (120, 362), (136, 300), (185, 226), (240, 182), (295, 216), (325, 288), (352, 312), (388, 232), (440, 190), (495, 226), (522, 300),
    (560, 330), (610, 312), (650, 252), (682, 186), (696, 150), (702, 124), (720, 130), (724, 150), (772, 160), (826, 190), (852, 222),
    (850, 246), (816, 252), (770, 246), (736, 252), (712, 302), (680, 372), (640, 428), (606, 476), (540, 492), (420, 506), (300, 496),
    (200, 482), (142, 450), (118, 402)], 8, closed=True)
# Two moments of a walk.
STRIDE = [
    limb([(172, 430), (186, 540), (160, 640), (170, 760), (178, 800)], [100, 60, 32, 26, 44]),
    limb([(200, 440), (252, 550), (272, 650), (306, 770), (314, 800)], [80, 50, 30, 24, 42]),
    limb([(590, 440), (616, 560), (640, 640), (690, 770), (702, 800)], [74, 42, 36, 24, 42]),
    limb([(556, 450), (536, 560), (522, 640), (500, 770), (490, 800)], [70, 40, 34, 24, 42]),
]
PASSING = [
    limb([(176, 436), (196, 545), (188, 645), (198, 770), (204, 800)], [100, 60, 32, 26, 44]),
    limb([(200, 440), (242, 550), (254, 650), (278, 770), (286, 800)], [80, 50, 30, 24, 42]),
    limb([(590, 440), (606, 560), (618, 640), (650, 770), (660, 800)], [74, 42, 36, 24, 42]),
    limb([(556, 450), (546, 560), (540, 640), (528, 770), (520, 800)], [70, 40, 34, 24, 42]),
]
TAIL = limb([(122, 362), (94, 430), (88, 500), (84, 540)], [20, 11, 12, 1])
LOAD = spline([(298, 324), (314, 262), (352, 236), (392, 262), (406, 324), (352, 346)], 8, closed=True)
STRAP = spline([(306, 300), (352, 316), (400, 300)], 8)
MIDDLE, FOOT = 468, 806

INK, CARGO, STRAP_COLOR = hexc("#1D0B29"), hexc("#F0662E"), hexc("#FFD98A")


def y_at(curve, x):
    return curve[min(range(len(curve)), key=lambda i: abs(curve[i][0] - x))][1]


class Camel:
    """One camel on a crest: its feet in the sand, leaning with the slope."""

    def __init__(self, x, scale, crest, legs=STRIDE):
        self.x, self.scale, self.legs = x, scale, legs
        self.base = y_at(crest, x) + 10 * scale
        self.angle = math.atan2(y_at(crest, x + 60) - y_at(crest, x - 60), 120) * 0.55

    def world(self, lx, ly):
        dx, dy = (lx - MIDDLE) * self.scale, (ly - FOOT) * self.scale
        c, s = math.cos(self.angle), math.sin(self.angle)
        return (self.x + dx * c - dy * s, self.base + dx * s + dy * c)

    def draw(self, ctx, rim=None):
        ctx.save()
        ctx.translate(self.x, self.base)
        ctx.rotate(self.angle)
        ctx.scale(self.scale, self.scale)
        ctx.translate(-MIDDLE, -FOOT)
        if rim is not None:
            # The sun on its back: the silhouette once more, a little up.
            ctx.save()
            ctx.translate(-3, -7)
            fill(ctx, BODY, rim)
            ctx.restore()
        for leg in self.legs:
            fill(ctx, leg, INK)
        fill(ctx, TAIL, INK)
        fill(ctx, BODY, INK)
        fill(ctx, LOAD, CARGO)
        stroke(ctx, STRAP, STRAP_COLOR, 9)
        ctx.restore()


def rope(ctx, follower, leader, width):
    """The lead rope from a camel's mouth to the tail of the one before it."""
    a, b = follower.world(846, 244), leader.world(112, 372)
    stroke(ctx, spline([a, ((a[0] + b[0]) / 2, (a[1] + b[1]) / 2 + 22), b], 8), INK, width)


# ---- The scene, on 1000 x 1000 ----------------------------------------------------------------------------------------


def squircle(ctx):
    c, n = 500, 5.0
    points = []
    for i in range(240):
        t = 2 * math.pi * i / 240
        ct, st = math.cos(t), math.sin(t)
        points.append((c + c * math.copysign(abs(ct) ** (2 / n), ct), c + c * math.copysign(abs(st) ** (2 / n), st)))
    path(ctx, points)


def sky(ctx):
    squircle(ctx)
    ctx.clip()
    g = cairo.LinearGradient(0, 0, 0, 860)
    for at, color in ((0, "#2C1A6E"), (0.42, "#8A2C7C"), (0.74, "#E4574F"), (1, "#FAAB4C")):
        g.add_color_stop_rgba(at, *hexc(color))
    ctx.rectangle(-10, -10, 1020, 1020)
    ctx.set_source(g)
    ctx.fill()


def sun(ctx, x, y, r, halo):
    if halo:
        g = cairo.RadialGradient(x, y, r - 12, x, y, r + 340)
        g.add_color_stop_rgba(0, *hexc("#FFD98A", 0.55))
        g.add_color_stop_rgba(0.4, *hexc("#FF9A5C", 0.2))
        g.add_color_stop_rgba(1, *hexc("#FF9A5C", 0.0))
        ctx.rectangle(-10, -10, 1020, 1020)
        ctx.set_source(g)
        ctx.fill()
    g = cairo.RadialGradient(x - r * 0.25, y - r * 0.3, r * 0.1, x, y, r)
    g.add_color_stop_rgba(0, *hexc("#FFF4C2"))
    g.add_color_stop_rgba(1, *hexc("#FFC766"))
    ctx.arc(x, y, r, 0, 2 * math.pi)
    ctx.set_source(g)
    ctx.fill()


def ground(ctx, curve, color):
    fill(ctx, curve + [(1040, 1040), (-40, 1040)], color)


def ripples(ctx, crest, color):
    """The wind's lines on the dune, swelling towards the light."""
    for i in range(1, 11):
        drop = i * 15 + i * i * 2.4
        line = [(x, y + drop + 9 * math.sin(x / 170 + i * 0.9)) for x, y in resample(crest, 100)]
        swell(ctx, line, [max(0.0, 4.4 - i * 0.18) * max(0.0, math.sin(math.pi * j / 99 + 0.2)) ** 1.1 * (0.35 + 0.65 * j / 99) for j in range(100)], color)


def caravan(ctx):
    """The logo: three camels."""
    near = spline([(-40, 806), (150, 760), (360, 734), (560, 716), (780, 690), (1040, 626)], 24)
    far = spline([(-40, 716), (230, 650), (520, 668), (800, 616), (1040, 566)], 20)
    sky(ctx)
    sun(ctx, 590, 450, 212, halo=True)
    ground(ctx, far, hexc("#8B2F5E"))
    last, mid, lead = Camel(136, 0.24, near), Camel(370, 0.36, near, PASSING), Camel(692, 0.47, near)
    last.draw(ctx)
    mid.draw(ctx)
    lead.draw(ctx, rim=hexc("#FFB46B", 0.9))
    rope(ctx, mid, lead, 3.2)
    rope(ctx, last, mid, 2.6)
    ground(ctx, near, hexc("#2A0F33"))
    # The light along the crest.
    swell(ctx, resample(near, 120), [3.6 * max(0.0, math.sin(math.pi * j / 119)) ** 0.7 * (0.3 + 0.7 * j / 119) for j in range(120)], hexc("#FFB46B"))
    ripples(ctx, near, hexc("#E9794A", 0.8))


def one_camel(ctx):
    """The small sizes: one camel against the sun."""
    crest = spline([(-40, 830), (300, 800), (700, 790), (1040, 750)], 20)
    sky(ctx)
    sun(ctx, 520, 420, 320, halo=False)
    Camel(490, 0.82, crest).draw(ctx)
    ground(ctx, crest, hexc("#7A2A5C"))


# ---- The files ---------------------------------------------------------------------------------------------------------


def drawn(draw, size, margin):
    """The drawing as an image of the size, the margin (of 1000) clear around its tile."""
    surface = cairo.ImageSurface(cairo.FORMAT_ARGB32, size, size)
    ctx = cairo.Context(surface)
    ctx.scale(size / 1000, size / 1000)
    ctx.translate(margin, margin)
    ctx.scale((1000 - 2 * margin) / 1000, (1000 - 2 * margin) / 1000)
    draw(ctx)
    # Cairo's pixels are premultiplied BGRA: straight RGBA for the files.
    pixels = bytearray(surface.get_data())
    for i in range(0, len(pixels), 4):
        b, g, r, a = pixels[i:i + 4]
        if 0 < a < 255:
            r, g, b = min(255, r * 255 // a), min(255, g * 255 // a), min(255, b * 255 // a)
        pixels[i:i + 4] = bytes((r, g, b, a))
    return Image.frombytes("RGBA", (size, size), bytes(pixels))


def main():
    # Margins: about what the old icon had; macOS's own icon grid leaves a tenth clear all round.
    margin = 30
    drawn(caravan, 256, margin).save(ASSETS / "karvon.png", optimize=True)
    drawn(one_camel, 64, margin).save(ASSETS / "karvon-small.png", optimize=True)
    drawn(caravan, 1024, 100).save(HERE / "karvon-macos.png", optimize=True)
    for size in SMALL_SIZES:
        drawn(one_camel, size, margin).save(HERE / f"karvon-{size}.png", optimize=True)

    icons = [drawn(one_camel, size, margin) for size in SMALL_SIZES] + [drawn(caravan, size, margin) for size in (128, 256)]
    icons[-1].save(ROOT / "deploy/windows/karvon.ico", format="ICO", sizes=[(i.width, i.height) for i in icons], append_images=icons[:-1])

    svg = ASSETS / "karvon.svg"
    surface = cairo.SVGSurface(str(svg), 256, 256)
    ctx = cairo.Context(surface)
    ctx.scale(0.256, 0.256)
    ctx.translate(margin, margin)
    ctx.scale((1000 - 2 * margin) / 1000, (1000 - 2 * margin) / 1000)
    caravan(ctx)
    surface.finish()


if __name__ == "__main__":
    main()
