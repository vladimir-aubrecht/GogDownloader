# Third-party dependencies

The source code in this repository is MIT-licensed. Dependency licenses remain their own.

## SteamKit2 3.4.0

- Project: <https://github.com/SteamRE/SteamKit>
- Package: <https://www.nuget.org/packages/SteamKit2/3.4.0>
- License: LGPL-2.1-only
- License text/source: <https://github.com/SteamRE/SteamKit/blob/3.4.0/SteamKit2/SteamKit2/license.txt>

SteamKit2 is referenced as a separate NuGet assembly by `SteamArchiver.Core`. Its transitive
dependencies and notices are listed in the resolved package assets and SteamKit2's bundled
`3rd party.txt`. Preserve those notices when distributing the Steam-capable application.
GOG-only and shared archive projects do not reference SteamKit2.

## innoextract (optional external executable)

- Project: <https://constexpr.org/innoextract/>
- License: GPLv2; see the upstream distribution for the complete terms

The GOG provider invokes an independently installed executable for Windows Inno installers.
It is not linked into the application or included in this repository's source tree. The Docker
image installs Debian's separate `innoextract` package; its license notices remain under
`/usr/share/doc/innoextract/`, and the corresponding source is available from Debian's package archive.
