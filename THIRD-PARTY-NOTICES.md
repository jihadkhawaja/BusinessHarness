# Third-party notices

The optional desktop application uses Avalonia 12.1.4 (MIT), distributed through
the centrally pinned Avalonia.Desktop, Avalonia.Themes.Fluent and
Avalonia.Fonts.Inter packages. Avalonia source and license:
https://github.com/AvaloniaUI/Avalonia/blob/master/licence.md . The Inter font is
licensed under the SIL Open Font License 1.1:
https://github.com/rsms/inter/blob/master/LICENSE.txt . Desktop native rendering
dependencies (including SkiaSharp and HarfBuzzSharp) retain their upstream licenses
and package notices. Desktop packages are not dependencies of the CLI tool.

Desktop publish output includes the project license, this notice, submodule
licenses and the vendored texts under `licenses/desktop`. Avalonia's text comes
from its pinned package source commit `2d79ba55fae1b1e71c69f93a668080c9029e6a31`;
Inter's OFL text comes from `rsms/inter` commit
`353b61b9f4430d5f420d56605a6e7993e0941470`. MicroCom's MIT text comes from package
source commit `76785efcafd91b5902fd19dd11145f6dd655b7b4`; Tmds.DBus uses source
commit `f0b2c29f57bdc7efaeccaa61ca9f13e9a0eebff7`. SkiaSharp 3.119.4,
HarfBuzzSharp 8.3.1.3, their shared native third-party notices and ANGLE
2.1.27548.20260419 license texts are preserved from the locked NuGet packages.
Desktop settings use Microsoft.Identity.Client.Extensions.Msal and
Microsoft.Identity.Client 4.91.1 (MIT, copyright Microsoft Corporation), source
commit `5df0316152afd7fd28a51ff046c5ba7f7829be04`. Their license is included at
`licenses/desktop/Microsoft.Identity.Client.txt`.

BusinessHarness builds MAGICore from the Git submodule at `external/MAGICore`, pinned to commit `ae8ba6bb1a8213066e7655581c5326fce93424c3` (v1.0.0).

MAGICore is licensed under Apache License 2.0. Its source and copyright/license notices are preserved in the submodule. The packaged tool and container include its license at `licenses/MAGICore/LICENSE`. BusinessHarness does not consume the MagiCore NuGet package. MAGICore's supporting Microsoft abstractions and tensor library remain ordinary, version-pinned NuGet dependencies.

Source: https://github.com/jihadkhawaja/MAGICore

BusinessHarness builds AgentConnector from the Git submodule at `external/AgentConnector`, pinned to commit `7b47d464c1698fddecdc505dfa78373064186253` (v0.1.5).

AgentConnector is licensed under Apache License 2.0. Its source and copyright/license notices are preserved in the submodule. The packaged tool and container include its license at `licenses/AgentConnector/LICENSE`. BusinessHarness uses the provider catalog and request builder, without hosting connector endpoints or enabling its credential store, OAuth runtime, proxies or write actions. Its private Sonar analyzer is centrally pinned; the library uses the .NET and ASP.NET Core shared frameworks.

Source: https://github.com/jihadkhawaja/AgentConnector

BusinessHarness builds the AgentSpace runtime from the Git submodule at
`external/AgentSpace`, pinned to commit `80c8e83fc38701679fb9cc8c89e80acc52c9eacb`.
Its three runtime libraries use the .NET base class library and native operating
system primitives. AgentSpace is licensed under Apache License 2.0. Its source
and copyright/license notices are preserved in the submodule. The packaged tool,
desktop app and container include its license at `licenses/AgentSpace/LICENSE`.

[AgentSpace source at the pinned revision](https://github.com/jihadkhawaja/AgentSpace/tree/80c8e83fc38701679fb9cc8c89e80acc52c9eacb)
