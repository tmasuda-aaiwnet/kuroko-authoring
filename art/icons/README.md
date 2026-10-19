# アイコン

Kuroko Authoring・Kuroko Converter と krk ファイルのアイコン（MIT。このリポジトリと同じ）。

| ファイル | 中身 |
|---|---|
| `kuroko-authoring.ico` | オーサリングツール（青のタイル・木の操り板から吊ったマトリョーシカ） |
| `kuroko-converter.ico` | 変換器（緑のタイル・「→」とマトリョーシカ） |
| `krk-file.ico` | krk ファイル（紙・木の台に立つマトリョーシカ） |
| `png/` | 各大きさの PNG（16・24・32・48・64・256、macOS 用に 128・512・1024。128 以上は 256 と同じ絵） |
| `svg/` | 描画の元（`preview.html` で見比べられる） |

16・24 は 1 点ずつ打ったドット絵、32 以上は図形。小さい大きさは縮小ではなく別に描いている（縮めると見分けられないため）。
256 以上は、黒子が操り板で人形を吊り、台に入れ子の人形が並ぶ場面。

作り直すとき：`python generate.py`（SVG と見比べのページ）→ `python build.py`（PNG と .ico。Chrome と ffmpeg を使う。場所は環境変数 `CHROME`・`FFMPEG` で指定でき、無ければ標準の場所と PATH から探す）。
macOS の .icns は `png/` の PNG から `iconutil` で作る。
