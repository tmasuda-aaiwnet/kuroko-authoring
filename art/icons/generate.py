import os, math
D = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'svg')  # SVG と見比べのページ（preview.html）の置き場

PAL = {
    'B': '#1E6FD9', 'b': '#1757B0', 'G': '#1E9E57', 'g': '#157A42',
    'T': '#C98A4B', 't': '#8C5A2B', '-': '#E8C79A',
    'R': '#D7192A', 'r': '#9E1020', 'K': '#1B1B1B', 'k': '#3A3A3A', 'S': '#FFDCC4',
    'W': '#FFFFFF', 'Y': '#F5C518', 'P': '#FFFFFF', 'F': '#D9D9D9', 'O': '#1B1B1B',
}
GP = 'shape-rendering="geometricPrecision"'


def wrap(n, body, crisp=False):
    sr = 'crispEdges' if crisp else 'geometricPrecision'
    return f'<svg xmlns="http://www.w3.org/2000/svg" width="{n}" height="{n}" viewBox="0 0 {n} {n}" shape-rendering="{sr}">{body}</svg>'


# ───────── ドット絵（16・24） ─────────
class Canvas:
    def __init__(self, n):
        self.n = n
        self.px = [['.'] * n for _ in range(n)]

    def put(self, x, y, c):
        if 0 <= x < self.n and 0 <= y < self.n and c != '.':
            self.px[y][x] = c

    def sprite(self, x0, y0, rows):
        for dy, row in enumerate(rows):
            for dx, c in enumerate(row):
                self.put(x0 + dx, y0 + dy, c)

    def rect(self, x, y, w, h, c):
        for yy in range(y, y + h):
            for xx in range(x, x + w):
                self.put(xx, yy, c)

    def vline(self, x, y0, y1, c):
        for y in range(y0, y1 + 1):
            self.put(x, y, c)

    def tile(self, c, corner=2):
        n = self.n
        self.rect(0, 0, n, n, c)
        # 角を丸く（透明に）
        cut = {2: [(0, 0), (1, 0), (0, 1)], 3: [(0, 0), (1, 0), (2, 0), (0, 1), (0, 2)]}[corner]
        for x, y in cut:
            for (px, py) in ((x, y), (n - 1 - x, y), (x, n - 1 - y), (n - 1 - x, n - 1 - y)):
                self.px[py][px] = '.'

    def paper(self, x, w, fold, table_h):
        n = self.n
        for y in range(n):
            for xx in range(x, x + w):
                if xx - x >= w - fold and y < (xx - (x + w - fold)):
                    continue  # 折れた角の外
                edge = xx in (x, x + w - 1) or y in (0, n - 1) or (xx - (x + w - fold)) == y
                self.put(xx, y, 'O' if edge else 'P')
        # 折り返し
        for i in range(1, fold):
            for j in range(i):
                self.put(x + w - fold + j, i, 'F')
        for i in range(fold):
            self.put(x + w - fold, i, 'O')
            self.put(x + w - fold + i, fold, 'O')
        # 木の台
        self.rect(x + 1, n - 1 - table_h, w - 2, 1, 't')
        self.rect(x + 1, n - table_h, w - 2, table_h - 1, 'T')

    def svg(self):
        out = []
        for y, row in enumerate(self.px):
            for x, c in enumerate(row):
                if c != '.':
                    out.append(f'<rect x="{x}" y="{y}" width="1" height="1" fill="{PAL[c]}"/>')
        return wrap(self.n, ''.join(out), crisp=True)


DOLL8 = ["..rRRr..",
         ".rRKKRr.",
         ".RKSSKR.",
         ".RSSSSR.",
         ".rRRRRr.",
         "rRRRRRRr",
         "RRWWWWRR",
         "RRWYYWRR",
         "RRWWWWRR",
         ".rRRRRr."]
DOLL6 = [".rRRr.",
         "rRKKRr",
         "RKSSKR",
         "rRSSRr",
         "rRRRRr",
         "RRWWRR",
         "RRWYRR",
         ".rRRr."]
DOLL12 = ["....rRRr....",
          "...rRRRRr...",
          "..rRKKKKRr..",
          "..RKKSSKKR..",
          "..RKSSSSKR..",
          "..RSKSSKSR..",
          "..rRSSSSRr..",
          "...rRRRRr...",
          "..rRRRRRRr..",
          ".rRRWWWWRRr.",
          ".RRWWYYWWRR.",
          ".RRWYrrYWRR.",
          ".RRWWYYWWRR.",
          ".rRRWWWWRRr.",
          "..rrRRRRrr.."]
ARROW7 = ["...W...",
          "...WW..",
          "WWWWWW.",
          "WWWWWWW",
          "WWWWWW.",
          "...WW..",
          "...W..."]
ARROW9 = ["....W....",
          "....WW...",
          "....WWW..",
          "WWWWWWWW.",
          "WWWWWWWWW",
          "WWWWWWWW.",
          "....WWW..",
          "....WW...",
          "....W...."]


def authoring_px(n):
    c = Canvas(n)
    if n == 16:
        c.tile('B', 2)
        c.rect(3, 2, 10, 1, 'T')            # 操り板（横）
        c.vline(7, 1, 3, 'T'); c.vline(8, 1, 3, 'T')  # 操り板（縦）
        c.vline(4, 3, 9, '-'); c.vline(11, 3, 9, '-')  # 糸
        c.vline(7, 4, 4, '-'); c.vline(8, 4, 4, '-')
        c.sprite(4, 5, DOLL8)
    else:
        c.tile('B', 3)
        c.rect(4, 2, 16, 2, 'T'); c.rect(4, 3, 16, 1, 't')
        c.rect(11, 1, 2, 5, 'T')
        c.vline(5, 4, 15, '-'); c.vline(18, 4, 15, '-')
        c.vline(11, 6, 7, '-'); c.vline(12, 6, 7, '-')
        c.sprite(6, 8, DOLL12)
    return c.svg()


def converter_px(n):
    c = Canvas(n)
    if n == 16:
        c.tile('G', 2)
        c.sprite(1, 4, ARROW7)
        c.sprite(9, 4, DOLL6)
    else:
        c.tile('G', 3)
        c.sprite(1, 7, ARROW9)
        c.sprite(11, 4, DOLL12)
    return c.svg()


def file_px(n):
    c = Canvas(n)
    if n == 16:
        c.paper(2, 12, 4, 4)
        c.sprite(4, 2, DOLL8)
    else:
        c.paper(3, 18, 6, 5)
        c.sprite(6, 4, DOLL12)
    return c.svg()


# ───────── 図形（32・48・256） ─────────
def shade(hexc, f):
    h = hexc.lstrip('#')
    r, g, b = (int(h[i:i + 2], 16) for i in (0, 2, 4))
    return '#%02X%02X%02X' % tuple(max(0, min(255, int(v * f))) for v in (r, g, b))


def doll(cx, top, h, color, detail=1):
    dark = shade(color, 0.7)
    sw = max(1, h * 0.035)
    body = (f'M{cx - 0.22 * h},{top + 0.44 * h} C{cx - 0.40 * h},{top + 0.60 * h} {cx - 0.38 * h},{top + h} {cx - 0.17 * h},{top + h} '
            f'L{cx + 0.17 * h},{top + h} C{cx + 0.38 * h},{top + h} {cx + 0.40 * h},{top + 0.60 * h} {cx + 0.22 * h},{top + 0.44 * h} Z')
    out = (f'<path d="{body}" fill="{color}" stroke="{dark}" stroke-width="{sw}" {GP}/>'
           + f'<circle cx="{cx}" cy="{top + 0.28 * h}" r="{0.28 * h}" fill="{color}" stroke="{dark}" stroke-width="{sw}" {GP}/>'
           + f'<ellipse cx="{cx}" cy="{top + 0.33 * h}" rx="{0.19 * h}" ry="{0.165 * h}" fill="{PAL["S"]}" {GP}/>'
           + f'<path d="M{cx - 0.195 * h},{top + 0.31 * h} Q{cx},{top + 0.10 * h} {cx + 0.195 * h},{top + 0.31 * h} Q{cx},{top + 0.23 * h} {cx - 0.195 * h},{top + 0.31 * h} Z" fill="{PAL["K"]}" {GP}/>'
           + f'<ellipse cx="{cx}" cy="{top + 0.75 * h}" rx="{0.19 * h}" ry="{0.19 * h}" fill="#FFF6E0" {GP}/>')
    # 前掛けの花：花びら 5 枚（中心は黄）。
    fc, fy, pr = cx, top + 0.75 * h, 0.055 * h
    for i in range(5):
        a = math.radians(-90 + 72 * i)
        out += f'<circle cx="{fc + math.cos(a) * pr * 1.25}" cy="{fy + math.sin(a) * pr * 1.25}" r="{pr}" fill="{color if color != PAL["R"] else "#E8344A"}" {GP}/>'
    out += f'<circle cx="{fc}" cy="{fy}" r="{pr * 0.8}" fill="{PAL["Y"]}" {GP}/>'
    if detail == 1:
        e = 0.024 * h
        out += (f'<circle cx="{cx - 0.06 * h}" cy="{top + 0.34 * h}" r="{e}" fill="{PAL["K"]}" {GP}/>'
                + f'<circle cx="{cx + 0.06 * h}" cy="{top + 0.34 * h}" r="{e}" fill="{PAL["K"]}" {GP}/>')
    if detail >= 2:
        e = 0.018 * h
        out += (f'<circle cx="{cx - 0.055 * h}" cy="{top + 0.34 * h}" r="{e}" fill="{PAL["K"]}" {GP}/>'
                + f'<circle cx="{cx + 0.055 * h}" cy="{top + 0.34 * h}" r="{e}" fill="{PAL["K"]}" {GP}/>'
                + f'<circle cx="{cx - 0.09 * h}" cy="{top + 0.385 * h}" r="{e * 1.6}" fill="#F7A1A1" {GP}/>'
                + f'<circle cx="{cx + 0.09 * h}" cy="{top + 0.385 * h}" r="{e * 1.6}" fill="#F7A1A1" {GP}/>'
                + f'<path d="M{cx - 0.03 * h},{top + 0.395 * h} Q{cx},{top + 0.42 * h} {cx + 0.03 * h},{top + 0.395 * h}" stroke="{PAL["K"]}" stroke-width="{e * 0.8}" fill="none" {GP}/>'
                + f'<ellipse cx="{cx - 0.16 * h}" cy="{top + 0.76 * h}" rx="{0.03 * h}" ry="{0.07 * h}" fill="#2E8B3A" transform="rotate(-20 {cx - 0.16 * h} {top + 0.76 * h})" {GP}/>'
                + f'<ellipse cx="{cx + 0.16 * h}" cy="{top + 0.76 * h}" rx="{0.03 * h}" ry="{0.07 * h}" fill="#2E8B3A" transform="rotate(20 {cx + 0.16 * h} {top + 0.76 * h})" {GP}/>')
    return out


# 木の十字の操り板（中心 cx,cy、長さ L、太さ t、傾き a）。
def cross(cx, cy, L, t, a=0):
    dark = PAL['t']
    out = ''
    for ang in (a, a + 90):
        out += (f'<rect x="{cx - L / 2}" y="{cy - t / 2}" width="{L}" height="{t}" rx="{t * 0.25}" fill="{PAL["T"]}" '
                f'stroke="{dark}" stroke-width="{max(1, t * 0.18)}" transform="rotate({ang} {cx} {cy})" {GP}/>')
    return out


def ends(cx, cy, L, a):
    pts = []
    for ang in (a, a + 90):
        rad = math.radians(ang)
        for s in (-1, 1):
            pts.append((cx + s * L / 2 * math.cos(rad), cy + s * L / 2 * math.sin(rad)))
    return pts


def string(x0, y0, x1, y1, w):
    return f'<line x1="{x0}" y1="{y0}" x2="{x1}" y2="{y1}" stroke="{PAL["-"]}" stroke-width="{w}" {GP}/>'


def string_dark(x0, y0, x1, y1, w):
    return f'<line x1="{x0}" y1="{y0}" x2="{x1}" y2="{y1}" stroke="#B8893F" stroke-width="{w}" {GP}/>'


def tile(n, color):
    inset = n * 0.03
    return f'<rect x="{inset}" y="{inset}" width="{n - 2 * inset}" height="{n - 2 * inset}" rx="{n * 0.17}" fill="{color}" {GP}/>'


def arrow(x, y, s, fill='#FFFFFF'):
    return (f'<polygon points="{x},{y + s * 0.36} {x + s * 0.5},{y + s * 0.36} {x + s * 0.5},{y + s * 0.08} {x + s},{y + s * 0.5} '
            f'{x + s * 0.5},{y + s * 0.92} {x + s * 0.5},{y + s * 0.64} {x},{y + s * 0.64}" fill="{fill}" {GP}/>')


def paper(n, x, w, fold, table_h, text=None):
    t = max(1, n * 0.035)
    outer = f'M{x},{t / 2} H{x + w - fold} L{x + w},{fold} V{n - t / 2} H{x} Z'
    out = (f'<path d="{outer}" fill="#FFFFFF" stroke="{PAL["O"]}" stroke-width="{t}" stroke-linejoin="round" {GP}/>'
           + f'<path d="M{x + w - fold},{t / 2} V{fold} H{x + w}" fill="{PAL["F"]}" stroke="{PAL["O"]}" stroke-width="{t}" stroke-linejoin="round" {GP}/>')
    ty = n - t - table_h
    out += (f'<rect x="{x + t / 2}" y="{ty}" width="{w - t}" height="{table_h * 0.28}" fill="{PAL["T"]}" {GP}/>'
            + f'<rect x="{x + t / 2}" y="{ty + table_h * 0.28}" width="{w - t}" height="{table_h * 0.72}" fill="{PAL["t"]}" {GP}/>')
    if text:
        out += (f'<text x="{x + w / 2}" y="{ty + table_h * 0.86}" text-anchor="middle" font-family="Arial Black, Arial" '
                f'font-weight="900" font-size="{table_h * 0.62}" fill="#FFFFFF" {GP}>{text}</text>')
    return out


# 黒子（256 用）：上が平らな背の高い頭巾、広い袖、黒い手。s＝場面の幅、(x0,y0) 左上。
def kuroko(x0, y0, s):
    P = lambda u, v: f'{x0 + u * s},{y0 + v * s}'
    K, k = PAL['K'], '#2C2C2C'
    out = ''
    # 袖（左右）：腕から垂れる広い袖
    out += f'<polygon points="{P(.36,.30)} {P(.20,.15)} {P(.14,.24)} {P(.20,.62)} {P(.36,.58)}" fill="{k}" {GP}/>'
    out += f'<polygon points="{P(.64,.30)} {P(.80,.15)} {P(.86,.24)} {P(.80,.62)} {P(.64,.58)}" fill="{k}" {GP}/>'
    # 胴
    out += f'<polygon points="{P(.35,.30)} {P(.65,.30)} {P(.67,.86)} {P(.33,.86)}" fill="{K}" {GP}/>'
    # 襟（重ねの線）と帯
    out += f'<polyline points="{P(.43,.31)} {P(.50,.52)} {P(.57,.31)}" fill="none" stroke="#5A5A5A" stroke-width="{.012 * s}" {GP}/>'
    out += f'<polyline points="{P(.46,.31)} {P(.50,.44)}" fill="none" stroke="#5A5A5A" stroke-width="{.008 * s}" {GP}/>'
    out += f'<rect x="{x0 + .345 * s}" y="{y0 + .62 * s}" width="{.31 * s}" height="{.07 * s}" fill="#3C3C3C" {GP}/>'
    # 頭巾：上が平ら、下が広がる。前に垂れる布。
    out += f'<polygon points="{P(.44,.00)} {P(.56,.00)} {P(.585,.34)} {P(.415,.34)}" fill="{K}" {GP}/>'
    out += f'<polygon points="{P(.455,.08)} {P(.545,.08)} {P(.555,.36)} {P(.445,.36)}" fill="{k}" {GP}/>'
    # 手
    for hx in (.19, .81):
        out += f'<rect x="{x0 + (hx - .035) * s}" y="{y0 + .085 * s}" width="{.07 * s}" height="{.075 * s}" rx="{.025 * s}" fill="{K}" stroke="#5A5A5A" stroke-width="{.006 * s}" {GP}/>'
    return out


def scene(x0, y0, s, table=True, text=None):
    out = kuroko(x0, y0, s)
    # 操り板と吊った人形（左＝赤、右＝紫）
    for hx, a, col, dx in ((.19, -18, '#D7192A', -0.06), (.81, 18, '#7B3FB5', 0.06)):
        cx, cy, L = x0 + hx * s, y0 + .13 * s, .2 * s
        dcx, dtop, dh = x0 + (hx + dx) * s, y0 + .34 * s, .25 * s
        for ex, ey in ends(cx, cy, L, a):
            if ey > cy - 0.02 * s:
                out += string_dark(ex, ey, dcx + (ex - cx) * 0.25, dtop + 0.03 * s, max(1, s * 0.005))
        out += cross(cx, cy, L, .035 * s, a)
        out += doll(dcx, dtop, dh, col, 2)
    # 台の上の人形（青・赤・緑）
    if table:
        ty = y0 + .84 * s
        out += doll(x0 + .36 * s, ty - .2 * s, .2 * s, '#1E5FC8', 2)
        out += doll(x0 + .64 * s, ty - .17 * s, .17 * s, '#2E9A44', 2)
        out += doll(x0 + .50 * s, ty - .27 * s, .27 * s, '#D7192A', 2)
    return out


def authoring_vec(n):
    if n == 256:
        body = (tile(n, PAL['B'])
                + f'<rect x="22" y="22" width="212" height="212" rx="22" fill="#EAF2FD" {GP}/>'
                + f'<rect x="22" y="188" width="212" height="46" rx="0" fill="{PAL["T"]}" {GP}/>'
                + f'<rect x="22" y="200" width="212" height="34" fill="{PAL["t"]}" {GP}/>'
                + f'<rect x="22" y="22" width="212" height="212" rx="22" fill="none" stroke="#EAF2FD" stroke-width="0" {GP}/>'
                + scene(30, 36, 196))
        # 下の角を丸く見せるため、パネルの外側を青で覆う
        body += (f'<path d="M22,212 V234 H44 A22,22 0 0 1 22,212 Z" fill="{PAL["B"]}" {GP}/>'
                 + f'<path d="M234,212 V234 H212 A22,22 0 0 0 234,212 Z" fill="{PAL["B"]}" {GP}/>')
        return wrap(n, body)
    s = n
    L, t = s * 0.62, s * 0.09
    cy = s * 0.15
    body = tile(n, PAL['B'])
    body += f'<rect x="{s / 2 - L / 2}" y="{cy - t / 2}" width="{L}" height="{t}" rx="{t * 0.3}" fill="{PAL["T"]}" stroke="{PAL["t"]}" stroke-width="{max(1, t * 0.2)}" {GP}/>'
    body += f'<rect x="{s / 2 - t / 2}" y="{s * 0.06}" width="{t}" height="{s * 0.2}" rx="{t * 0.3}" fill="{PAL["T"]}" stroke="{PAL["t"]}" stroke-width="{max(1, t * 0.2)}" {GP}/>'
    sw = max(1, s * 0.035)
    body += string(s / 2 - L / 2 + t * 0.5, cy, s * 0.29, s * 0.6, sw) + string(s / 2 + L / 2 - t * 0.5, cy, s * 0.71, s * 0.6, sw)
    body += doll(s / 2, s * 0.3, s * 0.64, PAL['R'], 2 if n >= 48 else 1)
    return wrap(n, body)


def converter_vec(n):
    s = n
    body = tile(n, PAL['G'])
    if n == 256:
        body += arrow(26, 92, 76)
        body += paper(n, 112, 122, 30, 50, 'KRK').replace(f'V{n - max(1, n * 0.035) / 2}', 'V230')  # 紙は少し短く
        body = tile(n, PAL['G']) + arrow(24, 96, 76) + _converter_doc()
        return wrap(n, body)
    body += arrow(s * 0.06, s * 0.3, s * 0.4)
    body += doll(s * 0.7, s * 0.14, s * 0.72, PAL['R'], 2 if n >= 48 else 1)
    return wrap(n, body)


def _converter_doc():
    x, y, w, h, f = 108, 26, 126, 204, 34
    t = 7
    out = (f'<path d="M{x},{y} H{x + w - f} L{x + w},{y + f} V{y + h} H{x} Z" fill="#FFFFFF" stroke="{PAL["O"]}" stroke-width="{t}" stroke-linejoin="round" {GP}/>'
           + f'<path d="M{x + w - f},{y} V{y + f} H{x + w}" fill="{PAL["F"]}" stroke="{PAL["O"]}" stroke-width="{t}" stroke-linejoin="round" {GP}/>')
    th = 46
    ty = y + h - t / 2 - th
    out += (f'<rect x="{x + t / 2}" y="{ty}" width="{w - t}" height="{th * 0.28}" fill="{PAL["T"]}" {GP}/>'
            + f'<rect x="{x + t / 2}" y="{ty + th * 0.28}" width="{w - t}" height="{th * 0.72}" fill="{PAL["t"]}" {GP}/>'
            + f'<text x="{x + w / 2}" y="{ty + th * 0.86}" text-anchor="middle" font-family="Arial Black, Arial" font-weight="900" font-size="{th * 0.6}" fill="#FFFFFF" {GP}>KRK</text>')
    out += doll(x + w / 2, ty - 120, 120, PAL['R'], 2)
    return out


def file_vec(n):
    if n == 256:
        x, w = 30, 196
        body = paper(n, x, w, 52, 58, 'KRK')
        body += scene(x + 10, 40, w - 20, table=True)
        return wrap(n, body)
    x, w = n * 0.12, n * 0.76
    th = n * (0.25 if n >= 48 else 0.2)
    body = paper(n, x, w, n * 0.22, th, 'KRK' if n >= 48 else None)
    dh = n - th - n * 0.1
    body += doll(n / 2, n - th - dh - n * 0.03 + n * 0.02, dh, PAL['R'], 2 if n >= 48 else 1)
    return wrap(n, body)


OUT = {}
for n in (16, 24):
    OUT[('authoring', n)] = authoring_px(n)
    OUT[('converter', n)] = converter_px(n)
    OUT[('file', n)] = file_px(n)
for n in (32, 48, 64, 256):
    OUT[('authoring', n)] = authoring_vec(n)
    OUT[('converter', n)] = converter_vec(n)
    OUT[('file', n)] = file_vec(n)
for (name, n), s in OUT.items():
    open(os.path.join(D, f'{name}_{n}.svg'), 'w', encoding='utf-8').write(s)

files = [('authoring', 'Kuroko Authoring'), ('converter', 'Kuroko Converter'), ('file', 'ドラマ第1話.krk')]
html = ['<html><head><meta charset="utf-8"><style>body{margin:0;font-family:"Yu Gothic UI","Segoe UI";font-size:12px}'
        '.blk{display:flex;gap:26px;padding:10px 14px;align-items:flex-start}.w{background:#fff;color:#000}.d{background:#202020;color:#eee}'
        '.list{min-width:150px}.list div{display:flex;align-items:center;gap:6px;height:20px;white-space:nowrap}'
        'canvas{image-rendering:pixelated}.col{display:flex;flex-direction:column;gap:6px}.row{display:flex;gap:8px;align-items:flex-end}</style></head><body>']
for cls in ('w', 'd'):
    html.append(f'<div class="blk {cls}"><div class="col"><div>小アイコン（16）</div><div class="list">')
    for name, label in files:
        html.append(f'<div><img src="{name}_16.svg">{label}</div>')
    html.append('</div></div><div class="col"><div>24・32・48（原寸）</div>')
    for name, _ in files:
        html.append('<div class="row">' + ''.join(f'<img src="{name}_{n}.svg">' for n in (24, 32, 48)) + '</div>')
    html.append('</div><div class="col"><div>16・24・32（4 倍に拡大）</div>')
    for name, _ in files:
        html.append('<div class="row">' + ''.join(
            f'<canvas data-src="{name}_{n}.svg" data-n="{n}" width="{n * 4}" height="{n * 4}"></canvas>' for n in (16, 24, 32)) + '</div>')
    html.append('</div><div class="col"><div>256（原寸）</div><div class="row">')
    html.append(''.join(f'<img src="{name}_256.svg">' for name, _ in files))
    html.append('</div></div></div>')
html.append('''<script>
document.querySelectorAll('canvas').forEach(c => {
  const n = +c.dataset.n, img = new Image();
  img.onload = () => { const t = document.createElement('canvas'); t.width = n; t.height = n;
    t.getContext('2d').drawImage(img, 0, 0, n, n);
    const g = c.getContext('2d'); g.imageSmoothingEnabled = false; g.drawImage(t, 0, 0, n * 4, n * 4); };
  img.src = c.dataset.src;
});
</script></body></html>''')
open(os.path.join(D, 'preview.html'), 'w', encoding='utf-8').write(''.join(html))
print('ok')
