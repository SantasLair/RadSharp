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

The samples publish to 3–4 MB executables plus about 18 MB of native libraries (mostly Skia).
