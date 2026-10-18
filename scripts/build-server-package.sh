#!/bin/sh
# Builds the self-contained club server package for the club VM (Ubuntu, no .NET installed there):
#   sh scripts/build-server-package.sh [runtime version] [output dir]
#   sh scripts/build-server-package.sh 10.0.13 ~/Документы/ClubInstall
# The runtime version takes .NET/ASP.NET Core runtime packs from NuGet when they are newer than the ones the installed
# SDK bundles (security releases reach NuGet before Ubuntu's dotnet-sdk package). Without it the SDK's own patch is
# used. Result: club-server-<commit>[-dirty]-linux-x64.tar.gz (files at the archive root, as setup-club-server.sh and
# the manual update in docs/install/club.md expect) and its sha256 line in SHA256SUMS of the output dir.
set -eu
RUNTIME=${1:-}
DEST=${2:-.}
cd "$(dirname "$0")/.."
COMMIT=$(git rev-parse --short HEAD)
git diff --quiet HEAD -- src || COMMIT="$COMMIT-dirty"
WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT
# -m:1: the dev box has 3 GB RAM
dotnet publish src/Club.Server -c Release -r linux-x64 --self-contained -m:1 -o "$WORK/pkg" ${RUNTIME:+-p:ClubRuntimeVersion=$RUNTIME}
grep -o '"runtimepack\.Microsoft\.[A-Za-z]*\.App\.Runtime\.linux-x64/[0-9.]*"' "$WORK/pkg/Club.Server.deps.json" | tr -d '"' | sort -u
NAME="club-server-$COMMIT-linux-x64.tar.gz"
tar -czf "$WORK/$NAME" -C "$WORK/pkg" .
mkdir -p "$DEST"
mv "$WORK/$NAME" "$DEST/$NAME"
cd "$DEST"
touch SHA256SUMS
grep -v " $NAME\$" SHA256SUMS > SHA256SUMS.new || true
sha256sum "$NAME" >> SHA256SUMS.new
mv SHA256SUMS.new SHA256SUMS
ls -la "$NAME"
grep " $NAME\$" SHA256SUMS
