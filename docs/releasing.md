# Releasing RadSharp

How a RadSharp release is built and published, by [`.github/workflows/release.yml`](../.github/workflows/release.yml).
This page is about releasing RadSharp itself; for shipping an app made with RadSharp, see
[distribution.md](distribution.md).

## One-time setup

1. On nuget.org, create an API key that can push `RadSharp.*` (glob pattern `RadSharp.*`, scope "Push new packages
   and package versions"). Optionally reserve the `RadSharp.` ID prefix for the account.
2. In the repository, **Settings › Environments › New environment** `nuget`:
   - **Required reviewers**: yourself. The nuget.org push then waits for an approval on every release.
   - **Deployment branches and tags**: selected tags, `v*`.
   - **Environment secrets**: `NUGET_API_KEY`, the key from step 1.

## Each release

1. Pick the version (SemVer): `0.1.0-preview.1`, `0.1.0-preview.2`, ... `1.0.0`. Never a `-dev` version: those mean a
   local pack, and the designer and template float to `0.1.0-dev*` for them.
2. Make sure `vs/RadSharp.VisualStudio/source.extension.vsixmanifest` has `Version="<MAJOR.MINOR.PATCH>"` (it must be
   numeric; the workflow checks it).
3. Write `docs/releases/<version>.md` (it becomes the GitHub release text) and add an entry to
   [CHANGELOG.md](../CHANGELOG.md).
4. Optional dry run: **Actions › Release › Run workflow** with the version. It runs CI and builds every artifact,
   attached to the run, without a GitHub release or a nuget.org push.
5. Tag `main` and push the tag:

   ```sh
   git tag -a v0.1.0-preview.1 -m "RadSharp 0.1.0-preview.1"
   git push origin v0.1.0-preview.1
   ```

6. The workflow runs CI, then builds the packages, the designer (win-x64, linux-x64, osx-arm64) and the VSIX, and
   creates a **draft** GitHub release with all of them and `SHA256SUMS.txt`. A version with a `-suffix` is marked as a
   prerelease. A failed macOS designer build doesn't stop the release: build it on a Mac with
   `dotnet publish src/RadSharp.Designer -c Release -r osx-arm64 --self-contained -p:RadSharpVersion=<version>`, then
   tar it and attach it to the draft.
7. Check the draft: download the designer for your OS and run it, install the VSIX, then **Publish release**.
8. Approve the `nuget` job (**Actions › the run › Review deployments**). It pushes `RadSharp.Core`,
   `RadSharp.Platform.Sdl` and `RadSharp.Templates`. **A nuget.org version can be unlisted but never deleted or
   reused**, so approve only once the draft checks out. nuget.org takes a few minutes to index new packages.
9. Check it as a newcomer would: `dotnet new install RadSharp.Templates::<version>`, `dotnet new radsharp-app`,
   `dotnet run`.

If a step fails after the tag is pushed, fix it on `main`, delete the draft release and the tag
(`git push --delete origin v<version>`, `git tag -d v<version>`), and tag again. That's fine as long as nothing
reached nuget.org; once it has, use the next version.
