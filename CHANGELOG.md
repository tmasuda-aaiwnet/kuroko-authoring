# 変更履歴 / Changelog

## v0.1.1

- Kuroko SDK：Matroska（mkv・webm）の読み出し（`MatroskaReader`）を追加。ffmpeg の libavformat を使わずに、Kuroko の mkv を読める。
  / Kuroko SDK: added a Matroska (mkv, webm) reader (`MatroskaReader`), so Kuroko's mkv files can be read without
  ffmpeg's libavformat.
- オーサリングツールの動作は v0.1.0 と変わらないため、このバージョンのビルド済みのもの（Releases）は公開しない。
  / The authoring tool behaves exactly as in v0.1.0, so no prebuilt packages (Releases) are published for this version.

## v0.1.0 — プレビュー / Preview

最初の公開。/ First public release.

- Kuroko SDK：形式の読み書き（`none`・`dvd-video-vm`・`kuroko-tiny`）、DVD-Video 互換の仮想マシン、ARIB 字幕の解析。
  / Kuroko SDK: reading and writing the format (`none`, `dvd-video-vm`, `kuroko-tiny`), a DVD-Video compatible virtual
  machine, and ARIB caption parsing.
- オーサリングツール：タイトル・音声・字幕（SRT・TS の ARIB 字幕）・章・メニュー・再生の流れ、AV1／H.265／H.264＋Opus での書き出し、
  タイトルごとのインターレースの扱い（フィールド分離・Weave・yadif・bwdif）。
  / Authoring tool: titles, audio, subtitles (SRT and ARIB captions in TS), chapters, menus and playback flow; export
  with AV1 / H.265 / H.264 and Opus; per-title interlace handling (separated fields, weave, yadif, bwdif).
