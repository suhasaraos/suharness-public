# Third-Party Notices

SuHarness is licensed under the Apache License, Version 2.0 (see `LICENSE`).

It depends on the third-party packages listed below. Each remains under its own
license and copyright. This file is provided for attribution when SuHarness is
redistributed in binary form; no third-party source code is vendored into this
repository.

Generated for the dependency closure of `Suharness.slnx` on .NET 10, as reported
by `dotnet list Suharness.slnx package --include-transitive`. The closure is 53
packages; every one of them is named below.

## Licenses in use

| License | Packages |
| --- | --- |
| MIT | 39 |
| Apache-2.0 | 12 |
| BSD-3-Clause | 1 |
| BSD-2-Clause | 1 |
| **Total** | **53** |

No package in the closure is licensed under the GPL, LGPL, AGPL, or any
source-available or proprietary license.

## MIT

- `Microsoft.Agents.AI`, `Microsoft.Agents.AI.Abstractions`, `Microsoft.Agents.AI.OpenAI` — Copyright (c) Microsoft Corporation
- `Microsoft.Extensions.AI`, `Microsoft.Extensions.AI.Abstractions`, `Microsoft.Extensions.AI.Evaluation`, `Microsoft.Extensions.AI.OpenAI` — Copyright (c) Microsoft Corporation
- `Microsoft.Extensions.Caching.Abstractions`, `Microsoft.Extensions.Compliance.Abstractions`, `Microsoft.Extensions.Configuration.Abstractions`, `Microsoft.Extensions.DependencyInjection.Abstractions`, `Microsoft.Extensions.Diagnostics.Abstractions`, `Microsoft.Extensions.FileProviders.Abstractions`, `Microsoft.Extensions.FileSystemGlobbing`, `Microsoft.Extensions.Hosting.Abstractions`, `Microsoft.Extensions.Logging.Abstractions`, `Microsoft.Extensions.ObjectPool`, `Microsoft.Extensions.Options`, `Microsoft.Extensions.Primitives`, `Microsoft.Extensions.VectorData.Abstractions` — Copyright (c) Microsoft Corporation
- `Microsoft.ApplicationInsights`, `Microsoft.AspNetCore.App.Internal.Assets`, `Microsoft.Bcl.AsyncInterfaces` — Copyright (c) Microsoft Corporation
- `Microsoft.ML.Tokenizers` — Copyright (c) Microsoft Corporation
- `Microsoft.NET.Test.Sdk`, `Microsoft.TestPlatform.ObjectModel`, `Microsoft.TestPlatform.TestHost`, `Microsoft.CodeCoverage` — Copyright (c) Microsoft Corporation
- `Microsoft.Testing.Extensions.Telemetry`, `Microsoft.Testing.Extensions.TrxReport.Abstractions`, `Microsoft.Testing.Platform`, `Microsoft.Testing.Platform.MSBuild` — Copyright (c) Microsoft Corporation
- `Microsoft.Win32.Registry`, `System.ClientModel`, `System.Memory.Data`, `System.Numerics.Tensors`, `System.Security.AccessControl` — Copyright (c) Microsoft Corporation
- `OpenAI` — Copyright (c) OpenAI
- `coverlet.collector` — Copyright (c) Toni Solarin-Sodara

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

## Apache-2.0

- `ModelContextProtocol`, `ModelContextProtocol.Core` — Copyright (c) Anthropic and contributors
- `xunit.analyzers`, `xunit.runner.visualstudio`, `xunit.v3`, `xunit.v3.assert`, `xunit.v3.common`, `xunit.v3.core.mtp-v2`, `xunit.v3.extensibility.core`, `xunit.v3.mtp-v2`, `xunit.v3.runner.common`, `xunit.v3.runner.inproc.console` — Copyright (c) .NET Foundation and Contributors

These are distributed under the same license as SuHarness itself; see `LICENSE`.

## BSD-3-Clause

- `Google.Protobuf` — Copyright 2008 Google Inc.

```
Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

    * Redistributions of source code must retain the above copyright notice,
this list of conditions and the following disclaimer.
    * Redistributions in binary form must reproduce the above copyright
notice, this list of conditions and the following disclaimer in the
documentation and/or other materials provided with the distribution.
    * Neither the name of Google Inc. nor the names of its contributors may be
used to endorse or promote products derived from this software without
specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

## BSD-2-Clause

- `Markdig` — Copyright (c) Alexandre Mutel

```
Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.
2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

## Fonts

The web UI links **DM Sans** and **IBM Plex Mono** from the Google Fonts CDN. No
font files are embedded or redistributed by this repository. Both faces are
available under the SIL Open Font License 1.1 should they ever be vendored.

## Template attribution

Portions of the UI scaffolding originate from the ASP.NET Core Blazor Web App
project template (MIT, Copyright (c) Microsoft Corporation).
