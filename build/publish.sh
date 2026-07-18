#!/usr/bin/env bash
#
# Copyright (c) 2025-2026 Aegis AO Soft LLC and Alexander Orlov.
# 34 Middletown Ave, Atlantic Highlands, NJ 07716
#
# THIS SOFTWARE IS THE CONFIDENTIAL AND PROPRIETARY INFORMATION OF
# Aegis AO Soft LLC and Alexander Orlov.
#
# This code may be used, reproduced, modified, or distributed ONLY with the
# prior written permission of Aegis AO Soft LLC / Alexander Orlov.
#
# Author: Alexander Orlov
# Aegis AO Soft LLC
#
# Same job as publish.ps1, for macOS and Linux build hosts.
# Trimming stays off on purpose: ICU culture lookup and Roslyn both use reflection.

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CLI="$ROOT/src/Aegis.Localizer.Cli/Aegis.Localizer.Cli.csproj"
WEB="$ROOT/src/Aegis.Localizer.Web/Aegis.Localizer.Web.csproj"
ARTIFACTS="$ROOT/artifacts"
RUNTIMES=("${@:-}")

if [ -z "${RUNTIMES[0]:-}" ]; then
  RUNTIMES=(win-x64 osx-arm64 osx-x64 linux-x64)
fi

rm -rf "$ARTIFACTS"
mkdir -p "$ARTIFACTS"

echo "Running tests..."
dotnet test "$ROOT/Aegis.Localizer.sln" -v q --nologo

echo "Packing the dotnet tool..."
dotnet pack "$CLI" -c Release -o "$ARTIFACTS" -v q --nologo

for rid in "${RUNTIMES[@]}"; do
  echo "Publishing $rid..."
  out="$ARTIFACTS/$rid"

  dotnet publish "$CLI" \
    -c Release \
    -r "$rid" \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true \
    -p:DebugType=none \
    -o "$out" -v q --nologo

  # The graphical interface is the same web app, shipped in a ui/ folder next to the CLI, which is
  # where UiCommand looks for it. Without this, `aegis-localizer ui` fails on every install.
  dotnet publish "$WEB"     -c Release     -r "$rid"     --self-contained false     -p:DebugType=none     -o "$out/ui" -v q --nologo

  # appsettings.json ships empty; a key committed into an artifact would be a leak.
  rm -f "$out/appsettings.local.json"

  (cd "$out" && zip -qr "$ARTIFACTS/aegis-localizer-$rid.zip" .)
  rm -rf "$out"
  echo "  -> $ARTIFACTS/aegis-localizer-$rid.zip"
done

echo
ls -lh "$ARTIFACTS"
