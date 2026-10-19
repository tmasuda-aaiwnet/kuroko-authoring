# generate.py の SVG から、全部の大きさの PNG（png/）と .ico（ここ）・macOS 用の大きい PNG（512・1024）を作る。
# 描くのは Chrome（headless）、切り出しは ffmpeg。場所は環境変数 CHROME・FFMPEG で指定する（無ければ標準の場所・PATH）。
import os, subprocess, struct, sys
HERE = os.path.dirname(os.path.abspath(__file__))
D = os.path.join(HERE, 'svg')   # generate.py が書いた SVG
CHROME = os.environ.get('CHROME') or (
    r'C:\Program Files\Google\Chrome\Application\chrome.exe' if os.name == 'nt'
    else '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome' if sys.platform == 'darwin'
    else 'google-chrome')
FF = os.environ.get('FFMPEG') or 'ffmpeg'
NAMES = ('authoring', 'converter', 'file')
SIZES = (16, 24, 32, 48, 64, 256)
BIG = (128, 512, 1024)   # 256 の絵を拡大・縮小して描く（macOS の iconset 用）
OUT = os.path.join(HERE, 'png'); os.makedirs(OUT, exist_ok=True)


def shot(html, w, h, png):
    p = os.path.join(D, '_tmp.html')
    open(p, 'w', encoding='utf-8').write(html)
    subprocess.run([CHROME, '--headless', '--disable-gpu', '--hide-scrollbars', '--allow-file-access-from-files',
                    '--default-background-color=00000000', '--force-device-scale-factor=1', f'--window-size={w},{h}',
                    f'--screenshot={png}', 'file:///' + p.replace(os.sep, '/')], check=True, capture_output=True)


def crop(src, x, y, w, h, dst):
    subprocess.run([FF, '-v', 'error', '-y', '-i', src, '-vf', f'crop={w}:{h}:{x}:{y},format=rgba', dst], check=True)


cell = 280
imgs = ''.join(f'<img src="{nm}_{n}.svg" style="position:absolute;left:{c * cell}px;top:{r * cell}px">'
               for r, nm in enumerate(NAMES) for c, n in enumerate(SIZES))
sheet = os.path.join(HERE, '_sheet.png')
shot(f'<html><body style="margin:0;background:transparent">{imgs}</body></html>', cell * len(SIZES), cell * len(NAMES), sheet)
for r, nm in enumerate(NAMES):
    for c, n in enumerate(SIZES):
        crop(sheet, c * cell, r * cell, n, n, os.path.join(OUT, f'{nm}_{n}.png'))
for nm in NAMES:
    for n in BIG:
        big = os.path.join(HERE, '_big.png')
        shot(f'<html><body style="margin:0;background:transparent"><img src="{nm}_256.svg" width="{n}" height="{n}" style="position:absolute;left:0;top:0"></body></html>', n, n, big)
        crop(big, 0, 0, n, n, os.path.join(OUT, f'{nm}_{n}.png'))

# .ico：各大きさの PNG をそのまま入れる（Windows Vista 以降は PNG の項目を読める）。
names_ico = {'authoring': 'kuroko-authoring.ico', 'converter': 'kuroko-converter.ico', 'file': 'krk-file.ico'}
for nm, ico in names_ico.items():
    datas = [open(os.path.join(OUT, f'{nm}_{n}.png'), 'rb').read() for n in SIZES]
    head = struct.pack('<HHH', 0, 1, len(SIZES))
    off = 6 + 16 * len(SIZES)
    ents = b''
    for n, d in zip(SIZES, datas):
        ents += struct.pack('<BBBBHHII', n % 256, n % 256, 0, 0, 1, 32, len(d), off)
        off += len(d)
    open(os.path.join(HERE, ico), 'wb').write(head + ents + b''.join(datas))
for t in ('_sheet.png', '_big.png', os.path.join('svg', '_tmp.html')):
    try: os.remove(os.path.join(HERE, t))
    except OSError: pass
print('ok')
