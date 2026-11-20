#!/bin/bash
# The CI test job, for a local Mac (macOS is not in the PR matrix; see .github/workflows/ci.yml).
# Run from the repo root on the Mac:  tools/mac-check.sh [--designer] [extra dotnet test args]
#   builds the solution, runs the tests (same flags as CI), then the NativeAOT builds of the samples.
#   --designer also publishes the self-contained designer to artifacts/designer (what CI uploads per OS).
# Needs the .NET SDK from global.json and the Xcode command-line tools (NativeAOT links with clang).
set -u
cd "$(dirname "$0")/.." || exit 2
DESIGNER=0
if [ "${1:-}" = "--designer" ]; then DESIGNER=1; shift; fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
start=$(date +%s)
step() { echo "== $1 ($(( $(date +%s) - start )) s)"; }

step "$(git log --oneline -1 2>/dev/null)"
step "build"
dotnet build RadSharp.slnx --configuration Release -v q -nologo || { step "BUILD FAILED"; exit 1; }

step "test"
dotnet test RadSharp.slnx --configuration Release --no-build \
  --blame-hang --blame-hang-timeout 5m --blame-hang-dump-type none "$@" || { step "TESTS FAILED"; exit 1; }

for s in Hello Calculator TextEditor SettingsDialog TileEditor; do
  step "NativeAOT $s"
  dotnet publish "samples/$s" --configuration Release --use-current-runtime -v q -nologo \
    --output "artifacts/samples/$s" || { step "NATIVEAOT FAILED: $s"; exit 1; }
done

if [ "$DESIGNER" = 1 ]; then
  step "designer"
  dotnet publish src/RadSharp.Designer --configuration Release --use-current-runtime --self-contained -v q -nologo \
    --output artifacts/designer || { step "DESIGNER FAILED"; exit 1; }
fi

step "ALL PASSED"
