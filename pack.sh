#!/usr/bin/env sh
# Packs RadSharp.Core, RadSharp.Platform.Sdl and RadSharp.Templates into artifacts/packages with ONE version.
# Usage: ./pack.sh [version] [configuration]   (default 0.1.0-dev.<yyyyMMddHHmm> UTC, Release)
# Use this instead of separate `dotnet pack` runs: they could straddle a minute and produce a Platform.Sdl that
# depends on a RadSharp.Core version that was never packed.
set -eu
cd "$(dirname "$0")"
version="${1:-0.1.0-dev.$(date -u +%Y%m%d%H%M)}"
configuration="${2:-Release}"
for project in src/RadSharp.Core src/RadSharp.Platform.Sdl templates/RadSharp.Templates.csproj; do
  dotnet pack "$project" --configuration "$configuration" "-p:RadSharpVersion=$version"
done
echo "Packed RadSharp $version into artifacts/packages"
