# 第三者のソフトウェア

Kuroko Authoring（`src/Kuroko.Authoring`）は、次のソフトウェアを使っています。Kuroko SDK（`src/Kuroko.Sdk`）と
`src/Kuroko.Authoring.Core` は .NET の標準ライブラリだけを使います（SDK には、下の「SDK に含むもの」を移植してあります）。

| ソフトウェア | ライセンス | 入手元 |
|---|---|---|
| Avalonia（Avalonia・Avalonia.Desktop・Avalonia.Themes.Fluent・Avalonia.Skia・Avalonia.HarfBuzz・Avalonia.Win32・Avalonia.Native・Avalonia.X11・Avalonia.FreeDesktop など） | MIT | https://github.com/AvaloniaUI/Avalonia |
| Avalonia.Fonts.Inter（パッケージ） | MIT | https://github.com/AvaloniaUI/Avalonia |
| Inter（上のパッケージに入っているフォント） | SIL Open Font License 1.1 | https://github.com/rsms/inter |
| ANGLE（Avalonia.Angle.Windows.Natives。Windows の描画） | BSD 3 条項（下に全文） | https://chromium.googlesource.com/angle/angle |
| SkiaSharp | MIT | https://github.com/mono/SkiaSharp |
| Skia（SkiaSharp に入っている描画ライブラリ） | BSD 3 条項 | https://skia.org/ |
| HarfBuzzSharp | MIT | https://github.com/mono/SkiaSharp |
| HarfBuzz（HarfBuzzSharp に入っている文字組みライブラリ） | Old MIT | https://github.com/harfbuzz/harfbuzz |
| MicroCom.Runtime | MIT | https://github.com/kekekeks/MicroCom |
| Tmds.DBus.Protocol（Linux） | MIT | https://github.com/tmds/Tmds.DBus |
| .NET | MIT | https://github.com/dotnet/runtime |

**ffmpeg はこのツールに入っていません。** 書き出しのときに、使う人が入れた ffmpeg を別の実行ファイルとして呼びます
（ffmpeg のライセンスは、その ffmpeg の配布元に従います）。

配布用のパッケージ（実行ファイル一式）を作るときは、上の各ライセンスの全文を一緒に入れること。

## SDK に含むもの（移植したもの）

| もの | ライセンス | 使っている所 |
|---|---|---|
| libaribcaption（https://github.com/xqq/libaribcaption） | MIT（Copyright (c) 2022 magicxqq） | ARIB 字幕の文字・色・外字の表（`src/Kuroko.Sdk/Arib/AribCaptionTables.cs`）と、字幕データの解釈（`AribCaptionDecoder.cs`） |
| ariblib（https://github.com/youzaka/ariblib） | MIT（Copyright (c) 2013 youzaka） | ARIB の 8 単位符号の文字列の解釈（`AribB24Decoder.cs`）と、追加記号の対応表（`AribGaiji.cs`） |
| epgdump_py（Yasumasa Murakami） | MIT（Copyright (C) 2011 Yasumasa Murakami） | ariblib を通じて、追加記号の対応表と文字列の解釈の考え方（`AribGaiji.cs`・`AribB24Decoder.cs`）。元の配布先（github.com/murakamiy/epgdump_py）は現在公開されていない。ライセンスは、作者のコミットごと残っている写し（https://github.com/aoi-sakura/epgdump_py）の LICENSE で確認した |

追加記号の対応表は、ARIB STD-B24 の表（追加記号集合の符号値と Unicode の対応）と照合し、足りない分を補っています。

## MIT License（libaribcaption）

```
MIT License

Copyright (c) 2022 magicxqq

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## The MIT License (MIT)（ariblib）

```
The MIT License (MIT)

Copyright (c) 2013 youzaka

Permission is hereby granted, free of charge, to any person obtaining a copy of
this software and associated documentation files (the "Software"), to deal in
the Software without restriction, including without limitation the rights to
use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of
the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS
FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER
IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

## MIT License（epgdump_py）

```
Copyright (C) 2011 Yasumasa Murakami. All Rights Reserved.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.
```

## ANGLE

```
Copyright 2018 The ANGLE Project Authors.
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions
are met:

    Redistributions of source code must retain the above copyright
    notice, this list of conditions and the following disclaimer.

    Redistributions in binary form must reproduce the above
    copyright notice, this list of conditions and the following
    disclaimer in the documentation and/or other materials provided
    with the distribution.

    Neither the name of TransGaming Inc., Google Inc., 3DLabs Inc.
    Ltd., nor the names of their contributors may be used to endorse
    or promote products derived from this software without specific
    prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
"AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS
FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE
COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT,
INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING,
BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT
LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN
ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
POSSIBILITY OF SUCH DAMAGE.
```
