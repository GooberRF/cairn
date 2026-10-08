# Third-party notices

Cairn is distributed with the following third-party components. Their licence texts are
reproduced in full below.

| Component | Version | Licence | Shipped |
|---|---|---|---|
| [AvalonEdit](https://github.com/icsharpcode/AvalonEdit) | 6.3.1.120 | MIT | yes |
| [Tomlyn](https://github.com/xoofx/Tomlyn) | 2.10.1 | BSD-2-Clause | yes |
| [NVorbis](https://github.com/NVorbis/NVorbis) | 0.10.5 | MIT | yes |
| [BCnEncoder.NET](https://github.com/Nominom/BCnEncoder.NET) | 2.3.0 | MIT (dual-licensed MIT OR Unlicense; used under MIT) | yes |
| [CommunityToolkit.HighPerformance](https://github.com/CommunityToolkit/dotnet) | 8.4.0 | MIT | yes (dependency of BCnEncoder.NET) |
| [Microsoft.Bcl.Numerics](https://github.com/dotnet/runtime) | 10.0.3 | MIT | yes (dependency of BCnEncoder.NET) |
| [libvorbis](https://xiph.org/vorbis/) | 1.3.7 | BSD-3-Clause | yes (in `cairn-vorbis.dll`) |
| [libogg](https://xiph.org/ogg/) | 1.3.5 | BSD-3-Clause | yes (in `cairn-vorbis.dll`) |
| [.NET runtime and WPF](https://github.com/dotnet/runtime) | 9.0 | MIT | in the self-contained build only (see below) |
| [xUnit.net](https://github.com/xunit/xunit) | 2.9.3 | Apache-2.0 | no (tests only) |
| [xunit.runner.visualstudio](https://github.com/xunit/visualstudio.xunit) | 3.1.5 | Apache-2.0 | no (tests only) |
| [Microsoft.NET.Test.Sdk](https://github.com/microsoft/vstest) | 18.10.1 | MIT | no (tests only) |

---

## AvalonEdit — MIT License

```
Copyright (c) AlphaSierraPapa for the SharpDevelop Team

Permission is hereby granted, free of charge, to any person obtaining a copy of this
software and associated documentation files (the "Software"), to deal in the Software
without restriction, including without limitation the rights to use, copy, modify, merge,
publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or
substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
DEALINGS IN THE SOFTWARE.
```

---

## NVorbis — MIT License

```
MIT License

Copyright (c) 2020 Andrew Ward

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

---

## BCnEncoder.NET — MIT License

Used for DXT1/DXT3/DXT5 block compression in the DDS converter. Its dependencies
CommunityToolkit.HighPerformance (Copyright (c) .NET Foundation and Contributors) and
Microsoft.Bcl.Numerics (Copyright (c) .NET Foundation and Contributors) are MIT-licensed under
the same terms as below.

```
MIT License

Copyright (c) 2020 Nominom

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

---

## Tomlyn — BSD 2-Clause License

```
Copyright (c) 2019, Alexandre Mutel
All rights reserved.

Redistribution and use in source and binary forms, with or without modification, are
permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this list of
   conditions and the following disclaimer.

2. Redistributions in binary form must reproduce the above copyright notice, this list of
   conditions and the following disclaimer in the documentation and/or other materials
   provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY
EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF
MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE
COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL,
EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF
SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION)
HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR
TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

---

## libvorbis and libogg — BSD 3-Clause License

Cairn writes Ogg Vorbis files with `cairn-vorbis.dll`, which is libvorbis 1.3.7 and libogg 1.3.5 built
unmodified from the Xiph.Org Foundation's source releases (https://xiph.org/downloads/). libvorbis:

```
Copyright (c) 2002-2020 Xiph.org Foundation

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions
are met:

- Redistributions of source code must retain the above copyright
notice, this list of conditions and the following disclaimer.

- Redistributions in binary form must reproduce the above copyright
notice, this list of conditions and the following disclaimer in the
documentation and/or other materials provided with the distribution.

- Neither the name of the Xiph.org Foundation nor the names of its
contributors may be used to endorse or promote products derived from
this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
``AS IS'' AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
A PARTICULAR PURPOSE ARE DISCLAIMED.  IN NO EVENT SHALL THE FOUNDATION
OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

libogg is under the same licence text with the notice `Copyright (c) 2002, Xiph.org Foundation`.

---

## .NET runtime and WPF — MIT License

```
Copyright (c) .NET Foundation and Contributors

Permission is hereby granted, free of charge, to any person obtaining a copy of this
software and associated documentation files (the "Software"), to deal in the Software
without restriction, including without limitation the rights to use, copy, modify, merge,
publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or
substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
DEALINGS IN THE SOFTWARE.
```

Cairn is built in two flavours, and only one of them redistributes the runtime:

- **`.\publish.ps1`** (the default, published to `dist/`) is self-contained: the single
  `Cairn.exe` carries its own copy of the .NET 9 Desktop runtime and WPF, so the MIT licence
  above is redistributed with it. The runtime itself contains further open-source components
  (for example MsQuic and Brotli), listed with their licences in the .NET runtime's own notices:
  https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT and
  https://github.com/dotnet/wpf/blob/main/THIRD-PARTY-NOTICES.TXT.
- **`.\publish.ps1 -FrameworkDependent`** (published to `dist/framework-dependent/`) and a plain
  `dotnet build` do not include the runtime at all; they use the .NET 9 Desktop Runtime already
  installed on the machine, and redistribute no part of it.

AvalonEdit, Tomlyn, NVorbis, BCnEncoder.NET (with its two dependencies), libvorbis and libogg ship with both
flavours.

---

## Test-only dependencies — Apache License 2.0 and MIT

xUnit.net, xunit.runner.visualstudio and Microsoft.NET.Test.Sdk are used by the test projects and are
not shipped with the application. The full text of the Apache License 2.0 is available at
<https://www.apache.org/licenses/LICENSE-2.0>; Microsoft.NET.Test.Sdk is MIT, reproduced above.
