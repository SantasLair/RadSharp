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

The samples publish to 3–4 MB executables plus about 18 MB of native libraries (mostly Skia).
