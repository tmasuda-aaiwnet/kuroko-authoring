# Kuroko Authoring

[English](#english)

映像・画像・音声から、**メニュー付き・字幕付きの多タイトルの動画ファイル**（Kuroko 形式・`.krk`）を作るツールと、
Kuroko 形式を読み書きする SDK です。DVD のオーサリングツールのように、タイトル・章・音声・字幕・メニューを組み立てて、
1 つのファイルに書き出します。

- 状態：**開発中**
- ライセンス：[MIT](LICENSE)（第三者のソフトウェアは [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)）
- 形式の仕様：[kuroko-spec](https://github.com/tmasuda-aaiwnet/kuroko-spec)
- © 2026 Takao Masuda（増田システムデザイニング）

## できること

- **タイトル**：映像のファイルを並べてタイトルにする。映像の符号化（コーデック・画質・速さ・インターレースの扱い）は
  タイトルごとに決められる（足したときは「書き出しの設定」の値）。
- **音声**：映像のファイルの中の音声と、外部の音声ファイル（コメンタリーなど）を一つの一覧にし、並べ替える。中の音声は
  入れるかどうかを選べる。ビットレートは音声ごと。
- **字幕**：映像のファイルの中の字幕（今は TS の ARIB 字幕だけ）と、SRT の字幕（UTF-8／Shift_JIS）を、音声と同じように
  並べ替え・選ぶ。ARIB 字幕は Kuroko Converter で TS から変換したときと同じ形（テキスト型・外字つき）で入れる。
- **章**：時刻と名前で章を付ける（付けなければ元のファイルの章を使う）。
- **メニュー**：背景（画像か、ループする動画）と BGM の上にボタンを置く。ボタンは状態ごとの画像（通常・選ばれているとき・
  押した瞬間）で表す。上下左右の移動先と、押したときの動作（タイトルを再生・章から再生・別のメニュー・続きから・
  音声や字幕を選ぶ・再生を終える）を決める。一定時間操作が無いときの動作も決められる。
- **再生の流れ**：ファイルを開いたとき・再生器のメニューキー・タイトルが終わったときの動作を決める。
- **書き出し**：映像は AV1（既定）・H.265・H.264、音声は Opus に符号化して `.krk` にまとめる。

メニューのあるファイルはコントローラ `kuroko-tiny`、無いファイルは `none` として書き出します（形式の仕様を参照）。

## ダウンロード

ビルド済みのもの（.NET を同梱）は [Releases](https://github.com/tmasuda-aaiwnet/kuroko-authoring/releases) にあります。
展開して `Kuroko.Authoring`（Windows は `Kuroko.Authoring.exe`）を起動します。

- 署名はしていません。Windows のスマートアプリコントロールが有効だと起動できず、SmartScreen の警告が出ることがあります。
  macOS では、初回は Finder で右クリック→「開く」で起動してください。
- ffmpeg は同梱していません（下の「必要なもの」）。

## 必要なもの

- [.NET 10 SDK](https://dotnet.microsoft.com/)（ソースからビルドするとき）
- **ffmpeg と ffprobe**（書き出しに使う。このツールには入っていない）。**libsvtav1**（AV1）と **libopus**（Opus）を
  含むビルドが要る。
  - Windows：例えば https://github.com/BtbN/FFmpeg-Builds/releases の `ffmpeg-master-latest-win64-gpl` を展開し、
    `bin\ffmpeg.exe` の場所をツールの「ffmpeg」欄に入れる（PATH に入っていれば空でよい）。
  - macOS：`brew install ffmpeg`
  - Linux：配布元の ffmpeg が libsvtav1 を含まない場合は、含むビルドを入れる。

## ビルドと起動

```sh
dotnet build Kuroko.slnx
dotnet run --project src/Kuroko.Authoring
```

## 使い方

1. 「書き出しの設定…」で、映像と音声の既定値を決める（「全タイトルへ適用」で、足してあるタイトルもそろえられる）。
2. 「映像を追加…」でタイトルにする映像を選ぶ。タイトルを選ぶと、右側で名前・映像の設定・音声と字幕（並べ替え・
   中のものを入れるか・言語・名前・ビットレート）・章を編集できる（外部の音声・字幕は、見出し「音声」「字幕」の右の「追加…」で足す）。
3. 「背景を選んで追加…」でメニューを作る。メニューを選ぶと、右側で背景・BGM・ボタンを編集できる。プレビュー上の
   ボタンは、掴んで移動・右下の角を掴んで大きさを変えられる。
4. 「再生の流れ…」で、ファイルを開いたとき・メニューキー・タイトルが終わったときの動作を決める。
5. 「検査」で食い違い（無いメニューを指している、など）が無いか確かめる。
6. 「書き出し…」で `.krk` を書き出す。「書き出した krk を開く」で、krk に関連付けた再生器で開いて確かめる。

プロジェクト（作りかけの状態）は `.kap` として保存できる（元のファイルの場所を覚えるだけで、ファイルそのものは含まない）。

## 構成

| ディレクトリ | 中身 |
|---|---|
| `src/Kuroko.Sdk` | Kuroko 形式の読み書き（JSON のモデル・ZIP の容器・レイアウト・言語コード。コントローラ `none`・`dvd-video-vm`・`kuroko-tiny`）と、DVD-Video 互換の仮想マシン（`Kuroko.DvdVm`）、放送 TS の ARIB 字幕・番組情報の解析（`Kuroko.Arib`）。映像の復号・再生は含まない。 |
| `src/Kuroko.Authoring.Core` | オーサリングの中身（プロジェクトのモデル・検査・書き出し）。画面を持たない。 |
| `src/Kuroko.Authoring` | オーサリングツールの画面（Avalonia。Windows・macOS・Linux）。 |
| `samples` | 仕様のサンプル（kuroko-spec の `samples/`）を作るスクリプト（`dotnet run samples/make-samples.cs -- <出力フォルダ>`）。 |
| `art/icons` | アイコン（オーサリングツール・変換器・krk ファイル）と、その作り方。 |

## 変更の受け入れについて

このリポジトリのコードは、著作権者が別の条件（再生器 Vuecove）でも使っています。そのため、**今のところ外部からの
変更（プルリクエスト）は受け付けていません**。不具合の報告や提案は Issue で歓迎します（[CONTRIBUTING.md](CONTRIBUTING.md)）。

---

## English

A tool that builds **multi-title video files with menus and subtitles** (Kuroko format, `.krk`) from video, image and
audio files, together with an SDK for reading and writing the Kuroko format. Like a DVD authoring tool, it assembles
titles, chapters, audio, subtitles and menus, and writes them into a single file.

- Status: **in development**
- License: [MIT](LICENSE) (third-party software: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md))
- Format specification: [kuroko-spec](https://github.com/tmasuda-aaiwnet/kuroko-spec)
- The user interface is currently in Japanese only.

### Features

- **Titles** from video files, with per-title encoding settings (codec, quality, speed, interlace handling).
- **Audio**: audio tracks inside the source file and external audio files in one reorderable list; per-track bitrate.
- **Subtitles**: ARIB captions inside broadcast TS files and SRT files (UTF-8 / Shift_JIS).
- **Chapters**, **menus** (image or looping video background, BGM, buttons with per-state images, directional
  navigation and actions), and **playback flow** (first play, menu key, end of title).
- **Export**: AV1 (default), H.265 or H.264 video and Opus audio, written as `.krk` with the `kuroko-tiny` controller
  (with menus) or `none` (without menus).

### Download

Pre-built packages (self-contained, including .NET) are available on
[Releases](https://github.com/tmasuda-aaiwnet/kuroko-authoring/releases). They are not code-signed: with Windows Smart
App Control enabled they cannot be started, and SmartScreen may warn; on macOS, open the app the first time with
right-click → Open. ffmpeg is not included.

### Requirements

- **ffmpeg and ffprobe** with **libsvtav1** and **libopus** (not included; set the path in the "ffmpeg" field, or put
  them on PATH). For example the `ffmpeg-master-latest-win64-gpl` build from https://github.com/BtbN/FFmpeg-Builds/releases
  on Windows, or `brew install ffmpeg` on macOS.
- [.NET 10 SDK](https://dotnet.microsoft.com/) to build from source:

```sh
dotnet build Kuroko.slnx
dotnet run --project src/Kuroko.Authoring
```

### Contributing

The code in this repository is also used by the copyright holder under different terms (the Vuecove player), so
**pull requests are not accepted at this time**. Bug reports and suggestions are welcome as Issues
(see [CONTRIBUTING.md](CONTRIBUTING.md)).

© 2026 Takao Masuda (Masuda System Designing)
