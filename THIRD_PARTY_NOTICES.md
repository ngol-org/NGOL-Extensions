# Third-Party Notices

This repository bundles the source of the following third-party projects, and its build output
redistributes the following third-party binaries.

## NGOL core

The `NodeGraphModLab` submodule is not a third party. It is the same project by the same authors,
under the same MIT license as this repository, so it is not listed below.

Its own dependencies are third-party, and a build made here includes them. They are listed in the
submodule's `THIRD_PARTY_NOTICES.md`, next to its `LICENSE`. If you redistribute something you
built from this repository, carry both sets of notices with it.

## MIT License

The following are distributed under the MIT License, reproduced once below:

- **iced** ( https://github.com/icedland/iced ) — Copyright (C) 2018-present iced project and contributors
  — redistributed as `Iced.dll` by the `ngol.ext.code` extension package
- **MonoMod** ( https://github.com/MonoMod/MonoMod ) — Copyright 2026 0x0ade, DaNike
  — redistributed as `MonoMod*.dll` by the `ngol.ext.il` extension package
- **Mono.Cecil** ( https://github.com/jbevain/cecil ) — Copyright (c) Jb Evain
  — redistributed as `Mono.Cecil*.dll` by the `ngol.ext.il` extension package

```
MIT License

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

## BSD 2-Clause License

- **MinHook** ( https://github.com/TsudaKageyu/minhook ) — Copyright (C) 2009-2017 Tsuda Kageyu
- **Hacker Disassembler Engine 64 C** (bundled inside MinHook) — Copyright (c) 2008-2009 Vyacheslav Patkov

Bundled as source under `native/ngol_native/MinHook/`, and statically linked into `ngol_native.dll`
by the `ngol.ext.native-hook` extension package. The same notice ships next to that binary as
`LICENSE-minhook.md`.

```
Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions
are met:

 1. Redistributions of source code must retain the above copyright
    notice, this list of conditions and the following disclaimer.
 2. Redistributions in binary form must reproduce the above copyright
    notice, this list of conditions and the following disclaimer in the
    documentation and/or other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
"AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED
TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A
PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER
OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL,
EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO,
PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR
PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF
LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING
NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

---

## Not redistributed

The following are used but never shipped by this repository. They are fetched or built by the
reader, and their own notices apply.

- **NUnit** / **NUnit3TestAdapter** ( https://nunit.org/ ) — test-only dependency of `NgolExt.NativeHook.Tests`
- **pythonnet** ( https://github.com/pythonnet/pythonnet ) — fetched on demand by
  `bridges/NgolBlenderAddon/scripts/get_pythonnet.ps1`; whether to redistribute `Python.Runtime.dll`
  is left to whoever distributes a package
