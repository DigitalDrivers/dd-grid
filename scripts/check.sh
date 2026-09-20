#!/usr/bin/env bash
# Local quality gate: the unit tests, a build of everything, and a race against a real AssettoServer.
# Run from anywhere: scripts/check.sh
#
# Tests marked [GameFact] need Assetto Corsa installed and skip themselves where it is not; AC_ROOT
# points at another install. The race tests need a built AssettoServer: the pinned version is fetched and
# built once into ~/.cache/dd-grid, or DDGRID_SERVER_DLL points at one that is already there.
set -euo pipefail

cd "$(dirname "$0")/.."

VERSION="$(cat assettoserver.version)"
SRC="${ASSETTOSERVER_SRC:-$HOME/.cache/dd-grid/AssettoServer-v$VERSION}"
DLL="${DDGRID_SERVER_DLL:-$SRC/AssettoServer/bin/Release/net9.0/AssettoServer.dll}"

echo "== test"
dotnet test tests/DDGrid.Core.Tests --nologo

echo "== build"
dotnet build DDGrid.sln -c Release --nologo

if [ ! -f "$DLL" ]; then
  echo "== fetching AssettoServer v$VERSION to race against"
  if [ ! -d "$SRC/AssettoServer" ]; then
    mkdir -p "$(dirname "$SRC")"
    git clone --quiet --depth 1 --branch "v$VERSION" https://github.com/compujuckel/AssettoServer.git "$SRC"
  fi
  dotnet build "$SRC/AssettoServer/AssettoServer.csproj" -c Release --nologo
  DLL="$SRC/AssettoServer/bin/Release/net9.0/AssettoServer.dll"
fi

echo "== race against a real server"
DDGRID_SERVER_DLL="$DLL" dotnet test tests/DDGrid.RaceTests --nologo

echo "== all checks passed"
