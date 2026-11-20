# Distributing apps

`dotnet publish -c Release` (or the designer's **Build**) produces a native executable for the OS it runs on,
next to three native libraries: SDL3, SkiaSharp and HarfBuzzSharp. Ship the whole folder.

## Building for every OS

NativeAOT can't cross-compile between operating systems, so build each on its own OS. New projects (the designer's
**New Project** and `dotnet new radsharp-app`) include `.github/workflows/build.yml`, which does exactly that on
GitHub Actions from one push and uploads one package per OS as an artifact:

| OS | Artifact | Contents |
| --- | --- | --- |
| Windows | `<app>-<version>-win-x64.zip` | the publish folder (exe with icon and version resource, plus SDL3, SkiaSharp, HarfBuzzSharp DLLs) |
| macOS | `<app>-<version>-osx-arm64.zip`, `<app>-<version>-osx-x64.zip` | `<Product>.app` (`Contents/MacOS` holds the exe and dylibs, `Info.plist`, `Resources/<app>.icns`), zipped with `ditto` |
| Linux | `<app>-<version>-linux-x64.tar.gz` | the exe, the libraries, `<app>.desktop` and `<app>.png` |
| Linux | `<app>-<version>-linux-x64.AppImage` | built with a pinned, checksum-verified `appimagetool`; best effort (a failure does not fail the build, the tarball is the guaranteed Linux artifact) |

The workflow triggers on every push (and on **Run workflow**), so every push builds all three OS jobs; to build less
often, narrow the `on:` block at the top of `build.yml` (for example to tags). The repository root must hold exactly one
`.csproj`; the `meta` job fails with a clear message otherwise.

Nothing is hard-coded in the YAML: the app name (`AssemblyName`), `<Version>`, `<Product>` and `<BundleId>` come from
the `.csproj`. The same `.csproj` properties give the Windows exe its icon (`<ApplicationIcon Condition="Exists('app.ico')">`; if you delete `app.ico` the exe simply has no icon)
and version resource (`Version`, `Product`, `Company`, `Copyright`). Edit those, and replace `app.ico` (Windows) and
`app.png`, a 1024 x 1024 PNG (macOS `.icns` and Linux), to brand the app. New projects ship a neutral default icon.

## Signing and notarization

Each signing step runs only when its repository secret exists (Settings > Secrets and variables > Actions); without
them the build still succeeds and produces unsigned packages (macOS bundles are ad-hoc signed, which Apple silicon
needs to run at all). The secrets are exposed only to the import, sign and notarize steps, never to `dotnet publish`
(which runs the project's MSBuild targets and analyzers): a small step before them records whether the secret is set,
and the signing steps test that flag.

| Secret | Format | Used for |
| --- | --- | --- |
| `WINDOWS_CERT_PFX` | base64 of an Authenticode `.pfx` (`[Convert]::ToBase64String([IO.File]::ReadAllBytes('cert.pfx'))`) | `signtool sign /fd sha256 /tr ... /td sha256` on the exe |
| `WINDOWS_CERT_PASSWORD` | the `.pfx` password | same |
| `WINDOWS_TIMESTAMP_URL` | optional; default `http://timestamp.digicert.com` | RFC 3161 timestamp server |
| `MACOS_CERT` | base64 of a "Developer ID Application" `.p12` (`base64 -i cert.p12`) | imported into a temporary keychain; `codesign --options runtime --timestamp` |
| `MACOS_CERT_PASSWORD` | the `.p12` password | same |
| `MACOS_SIGN_IDENTITY` | e.g. `Developer ID Application: Name (TEAMID)` | `codesign --sign` |
| `APPLE_ID`, `APPLE_TEAM_ID`, `APPLE_APP_PASSWORD` | Apple ID email, 10-character team id, app-specific password | `xcrun notarytool submit --wait`, then `xcrun stapler staple` (needs the `MACOS_*` secrets too) |

Certificates cost money and need a human: an Authenticode certificate from a CA, and an Apple Developer Program
membership for Developer ID and notarization.

## Testing the workflow on a fork

You can't run the workflow locally. Push the project (or a fork of it) to GitHub, open the Actions tab and watch
the `Build` run (or start it with **Run workflow**, the `workflow_dispatch` trigger). Download the artifacts and check:
on Windows, the exe's Properties > Details tab shows the version and the icon; on macOS, unzip, open the `.app`
(right-click > Open if unsigned) and run `codesign -dv` on it; on Linux, unpack the tarball and run the exe, then
`chmod +x` and run the AppImage. To exercise signing, add the secrets to the fork first; remove them afterwards.

## Before users download it

- **Windows:** unsigned executables trigger SmartScreen warnings; sign with an Authenticode certificate (the workflow
  does when `WINDOWS_CERT_PFX` is set).
- **macOS:** Gatekeeper blocks unsigned downloads; the workflow signs and notarizes when the secrets above are set.
- **Linux:** distribute the tarball or AppImage (or a Flatpak); the app needs no runtime, only the three libraries.
  The tarball's `<app>.desktop` is not installed by unpacking it: its `Exec=<app>` and `Icon=<app>` are bare names, so
  to get a launcher entry copy it to `~/.local/share/applications/`, make `Exec` the absolute path of the exe and `Icon`
  the absolute path of `<app>.png` (or install the icon into the icon theme), or ship an installer/package that does so.

## Code protection

NativeAOT output is machine code: there is no IL for ILSpy to decompile, which is much harder to read than a
plain .NET or Python app. Form files are embedded as resources and remain readable (they describe layout, not
logic). Nothing shipped to a user is truly secret; the goal is "not trivially readable".

## Size

Measured on win-x64, .NET 10.0.404, SkiaSharp 4.152.1, Release.

**Apps (NativeAOT).** Hello, ControlsDemo and GridDemo publish to a 5.4-5.5 MB executable plus 17.6 MB of native
libraries (`libSkiaSharp.dll` 12.8 MB, `SDL3.dll` 2.9 MB, `libHarfBuzzSharp.dll` 1.9 MB). The folder also holds
debug symbols (`<App>.pdb`, 16-17 MB, plus two small RadSharp ones); leave every `*.pdb` out of what you ship, and
keep them if you want readable native crash stacks (the generated Windows workflow does delete the app's own `*.pdb`
from `out\` before zipping, so the "ship the whole folder" rule above applies to the zip, not to the raw publish folder).
The SkiaSharp and HarfBuzzSharp NuGet packages also carry their own native `.pdb` files (`libSkiaSharp.pdb` and
`libHarfBuzzSharp.pdb`, 116 MB for win-x64). The RadSharp build targets drop exactly the `.pdb` files that come from the
`SkiaSharp*` and `HarfBuzzSharp*` packages (matched by NuGet package id) from a publish; `.pdb` files from any other
package, the app's own and RadSharp's stay. `RadSharpKeepPackagePdbs=true` brings the dropped ones back. A whole sample
publish folder is 40 MB, down from 156 MB.

**The designer.** It runs on the JIT (it compiles your project with Roslyn for F9), so it is a normal .NET app.

| Publish | Size | Notes |
|---|---|---|
| `dotnet publish` with no `-r` | 694 MB | every OS's natives under `runtimes/`: never ship this |
| `-r win-x64 --self-contained` | 230 MB | 117 MB of it Skia/HarfBuzz native `.pdb` files |
| + no package `.pdb` files | 114 MB | now the default in `RadSharp.Core.targets` |
| + `SatelliteResourceLanguages=en` | 107 MB | Roslyn's 25 language folders (6.4 MB) go; these two are now the project default |
| + ReadyToRun | 127 MB | faster start |
| `-r win-x64 --no-self-contained` | 31 MB | needs the .NET 10 runtime installed |
| `-r win-x64 --no-self-contained -p:PublishReadyToRun=true` | 50 MB | fastest start (below) |

The self-contained 107 MB is: .NET runtime and libraries 77 MB, Skia/HarfBuzz 15 MB, Roslyn 9 MB, SDL 3 MB,
RadSharp 2.5 MB. Not done on purpose: `PublishTrimmed` (Roslyn and the live-run load context use reflection),
`InvariantGlobalization` (the grid sorts and parses numbers with the current culture; on Windows it would not save
anything anyway, ICU comes from the OS), single-file (the live run reads `TRUSTED_PLATFORM_ASSEMBLIES` and needs the
assemblies as files).

**Recommended designer recipe** (build on the target OS; the designer needs no cross-compile):

```
dotnet publish src/RadSharp.Designer -c Release -r win-x64 --no-self-contained -p:PublishReadyToRun=true -o out
```

(50 MB, needs the .NET 10 runtime, which anyone building C# apps has.) For a download that needs nothing installed,
use `--self-contained` with the same `-p:PublishReadyToRun=true` (127 MB). Use the RID of the OS you build on
(`osx-arm64`, `linux-x64`...): one RID per publish keeps the other OSes' natives out.

## Start-up

"Started" means the first frame presented. Set `RADSHARP_STARTUP_TRACE=1` and any app prints
`startup: N ms` (process start to first present) to stderr; it is off by default. Reproduce with
`tools/measure-startup.ps1 <path to exe>` (cold = first run of a fresh copy of the folder, so the file cache is
empty and the first-run scan happens; warm = median of 5 runs after). Publish the samples with
`dotnet publish samples/Hello -c Release -r win-x64` (on Windows, put the Visual Studio Installer folder on `PATH`
first, or the native link can't find vswhere).

| App | Cold | Warm (median of 5) |
|---|---|---|
| Hello (NativeAOT) | 190 ms | 156 ms |
| ControlsDemo (NativeAOT) | 231 ms | 197 ms |
| GridDemo (NativeAOT) | 211 ms | 195 ms |
| Designer, self-contained (JIT) | 1221 ms | 456 ms |
| Designer, self-contained + ReadyToRun | 756 ms | 327 ms |
| Designer, framework-dependent | 893 ms | 320 ms |
| Designer, framework-dependent + ReadyToRun | 447 ms | 250 ms |

Machine: i9-13900KF, 32 GB, NVMe SSD, Windows 11, Defender on. A single run varies by tens of milliseconds
(one warm run in each of the sample series was 300-470 ms), so read these as rough.
