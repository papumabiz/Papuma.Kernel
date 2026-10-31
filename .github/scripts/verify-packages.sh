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
#   - a package other than Papuma.Kernel.Core carries Papuma.Kernel.Core.dll (Core is a
#     package of its own; one copy per build, review B-4),
#   - a dependency on another Papuma package is not exactly [<version>] (the packages use
#     each other's internals, so only the same build is safe; Directory.Build.targets).

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
  Papuma.Kernel.Core
  Papuma.Kernel.FSharp
  Papuma.Kernel.Local
  Papuma.Kernel.Mcp
  Papuma.Kernel.Testing
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

for id in "${PACKAGES[@]}"; do
  f="${ARTIFACTS}/${id}.${VERSION}.nupkg"
  [ -f "$f" ] || continue

  if [ "$id" != "Papuma.Kernel.Core" ] && unzip -Z1 "$f" | grep -q 'Papuma\.Kernel\.Core\.dll$'; then
    echo "::error file=${f}::carries Papuma.Kernel.Core.dll, which belongs to the Papuma.Kernel.Core package only"
    failed=1
  fi

  # Every <dependency id="Papuma..." version="..."> of the nuspec must pin exactly this version.
  while read -r dependency; do
    [ -n "$dependency" ] || continue
    if [ "${dependency#* }" != "[${VERSION}]" ]; then
      echo "::error file=${f}::dependency ${dependency% *} is '${dependency#* }', expected '[${VERSION}]'"
      failed=1
    fi
  done < <(unzip -p "$f" '*.nuspec' \
    | sed -n 's/.*<dependency id="\(Papuma[^"]*\)" version="\([^"]*\)".*/\1 \2/p')
done

if [ "$failed" -eq 0 ] \
  && ! unzip -Z1 "${ARTIFACTS}/Papuma.Kernel.Core.${VERSION}.nupkg" | grep -qx 'lib/net10.0/Papuma.Kernel.Core.dll'; then
  echo "::error::Papuma.Kernel.Core.${VERSION}.nupkg lacks lib/net10.0/Papuma.Kernel.Core.dll"
  failed=1
fi

if [ "$failed" -ne 0 ]; then
  exit 1
fi

echo "${#PACKAGES[@]} package(s) verified for version ${VERSION}"
