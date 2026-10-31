#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# SPDX-FileCopyrightText: 2026 Harald Lapp
#
# Verifies the packed NuGet packages before anything is uploaded or published.
#
# Usage: verify-packages.sh <artifacts-dir> <version>
#
# Fails closed when
#   - a package of PACKAGES is missing as <id>.<version>.nupkg (exact file name,
#     no suffix match: v2.1.0 must not pass for 12.1.0),
#   - a .nupkg is present that is not in PACKAGES (a new or renamed package has
#     to be added here deliberately),
#   - a package that embeds Papuma.Kernel.Core lacks lib/net10.0/Papuma.Kernel.Core.dll
#     (Core is not packed on its own; see the comment in Papuma.Kernel.csproj).

set -euo pipefail

if [ "$#" -ne 2 ]; then
  echo "usage: $0 <artifacts-dir> <version>" >&2
  exit 2
fi

ARTIFACTS="$1"
VERSION="$2"

if [ -z "$VERSION" ]; then
  echo "::error::no version given"
  exit 2
fi

PACKAGES=(
  Papuma.Kernel
  Papuma.Kernel.AspNetCore
  Papuma.Kernel.FSharp
  Papuma.Kernel.Local
  Papuma.Kernel.Mcp
  Papuma.Kernel.Testing
)

# The packages whose csproj embeds the Core assembly (CopyProjectReferencesToPackage).
EMBEDS_CORE=(
  Papuma.Kernel
  Papuma.Kernel.FSharp
  Papuma.Kernel.Local
)

failed=0

for id in "${PACKAGES[@]}"; do
  if [ ! -f "${ARTIFACTS}/${id}.${VERSION}.nupkg" ]; then
    echo "::error::missing package ${id}.${VERSION}.nupkg"
    failed=1
  fi
done

shopt -s nullglob
for f in "${ARTIFACTS}"/*.nupkg; do
  name="$(basename "$f")"
  known=0
  for id in "${PACKAGES[@]}"; do
    if [ "$name" = "${id}.${VERSION}.nupkg" ]; then
      known=1
      break
    fi
  done
  if [ "$known" -eq 0 ]; then
    echo "::error file=${f}::unexpected package ${name} (not ${VERSION}, or not listed in verify-packages.sh)"
    failed=1
  fi
done

for id in "${EMBEDS_CORE[@]}"; do
  f="${ARTIFACTS}/${id}.${VERSION}.nupkg"
  [ -f "$f" ] || continue
  if ! unzip -Z1 "$f" | grep -qx 'lib/net10.0/Papuma.Kernel.Core.dll'; then
    echo "::error file=${f}::lib/net10.0/Papuma.Kernel.Core.dll is not embedded"
    failed=1
  fi
done

if [ "$failed" -ne 0 ]; then
  exit 1
fi

echo "${#PACKAGES[@]} package(s) verified for version ${VERSION}"
