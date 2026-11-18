# Distributing apps

`dotnet publish -c Release` (or the designer's **Build**) produces a native executable for the OS it runs on,
next to three native libraries: SDL3, SkiaSharp and HarfBuzzSharp. Ship the whole folder.

## Building for every OS

NativeAOT can't cross-compile between operating systems, so build each on its own OS. New projects include
`.github/workflows/build.yml`, which does exactly that on GitHub Actions (Windows, macOS, Linux) and uploads each
build as an artifact.

## Before users download it

- **Windows:** unsigned executables trigger SmartScreen warnings. Sign with an Authenticode certificate
  (`signtool sign /fd sha256 /tr <timestamp-url> /td sha256 MyApp.exe`).
- **macOS:** Gatekeeper blocks unsigned downloads. Put the executable and libraries in an `.app` bundle
  (`Contents/MacOS`), sign it with a Developer ID certificate (`codesign --deep --options runtime`), and notarize it
  (`xcrun notarytool submit … --wait`, then `xcrun stapler staple`). Signing needs an Apple Developer account.
- **Linux:** distribute as a folder, tarball, AppImage or Flatpak; the app needs no runtime, only the three libraries.

## Code protection

NativeAOT output is machine code: there is no IL for ILSpy to decompile, which is much harder to read than a
plain .NET or Python app. Form files are embedded as resources and remain readable (they describe layout, not
logic). Nothing shipped to a user is truly secret; the goal is "not trivially readable".

## Size

Measured on win-x64, .NET 10.0.404, SkiaSharp 4.152.1, Release.

**Apps (NativeAOT).** Hello, ControlsDemo and GridDemo publish to a 5.4-5.5 MB executable plus 17.6 MB of native
libraries (`libSkiaSharp.dll` 12.8 MB, `SDL3.dll` 2.9 MB, `libHarfBuzzSharp.dll` 1.9 MB). The folder also holds
debug symbols (`<App>.pdb`, 16-17 MB, plus two small RadSharp ones); leave every `*.pdb` out of what you ship, and
keep them if you want readable native crash stacks. The SkiaSharp and HarfBuzzSharp NuGet packages also carry
their own native `.pdb` files (116 MB for win-x64); the RadSharp build targets keep those out of a publish
(`RadSharpKeepPackagePdbs=true` brings them back). A whole sample publish folder is 40 MB, down from 156 MB.

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
