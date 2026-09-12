# Windows notification dependency license sources

Retrieved from the publishers on 2026-09-12. These files are checked into the
repository so packaging does not download license text or require a Windows SDK
installation. `scripts/build_windows.py` verifies their SHA-256 digests before
including them in a release.

- `WINDOWS-SDK-LICENSE.rtf`: the unmodified Microsoft Windows SDK license reached
  from the `licenseUrl` in Microsoft.Windows.SDK.NET.Ref 10.0.19041.57's NuGet
  metadata, <https://aka.ms/WinSDKLicenseURL>. The resolved publisher URL is
  <https://download.microsoft.com/download/0/F/F/0FF2B061-47DD-4F55-89B6-FD1D8C44F14D/sdk_license.rtf>.
  SHA-256: `dd07eb178e00c6bba4148457fc00ff77cd4887eb521d504186fe59c9ec8bbe62`.
- `CSWINRT-LICENSE.txt`: unmodified C#/WinRT MIT license from
  <https://github.com/microsoft/CsWinRT/blob/31d6ba283581160dc56ba4099958b3a7ba772c48/LICENSE>.
  This file's last change is the initial 2019 license commit; the SDK package
  currently distributes WinRT.Runtime.dll assembly version 2.2.0.0. SHA-256:
  `9906940f61b1f0b533fa7d99baf55178b2808fbe113ea51dfbfad8572ccd5f2b`.

Microsoft.Toolkit.Uwp.Notifications 7.1.3 includes its original `License.md`
(MIT, copyright .NET Foundation and Contributors) in the restored NuGet package.
Packaging copies that exact file and the publisher's `.nuspec`, rather than
replacing it with a generic MIT template. The Windows SDK `.nuspec` is preserved
as well. Published dependency versions and binaries come from `CodexUsage.deps.json`
and are cross-checked against `project.assets.json`, including SDK download dependencies.
