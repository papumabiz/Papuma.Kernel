#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# SPDX-FileCopyrightText: 2026 Harald Lapp
#
# Writes the GitHub release notes of a tag.
#
# Usage: release-notes.sh <tag> <output-file>
#
# Uses .github/release-notes/<tag>.md when it exists (curated notes); otherwise the
# CHANGELOG.md section of the version — from its "## <version>" heading to the next
# "## " heading — followed by a link to the changelog. Fails when neither exists, so a
# release never goes out with empty notes.

set -euo pipefail

if [ "$#" -ne 2 ]; then
  echo "usage: $0 <tag> <output-file>" >&2
  exit 2
fi

TAG="$1"
OUT="$2"
VERSION="${TAG#v}"
CURATED=".github/release-notes/${TAG}.md"

if [ -f "$CURATED" ]; then
  cp "$CURATED" "$OUT"
  echo "release notes: ${CURATED}"
  exit 0
fi

# The heading is "## 3.0.0" or "## 3.0.0 (date)"; index() keeps the version literal (no regex dots).
awk -v version="$VERSION" '
  { sub(/\r$/, "") }
  /^## / {
    if (inside) { exit }
    heading = substr($0, 4)
    if (heading == version || index(heading, version " ") == 1) { inside = 1; next }
  }
  inside { print }
' CHANGELOG.md > "$OUT"

if ! grep -q '[^[:space:]]' "$OUT"; then
  echo "::error::no ${CURATED} and no '## ${VERSION}' section in CHANGELOG.md"
  exit 1
fi

{
  echo
  echo "Full changelog: https://github.com/${GITHUB_REPOSITORY:-papumabiz/Papuma.Kernel}/blob/${TAG}/CHANGELOG.md"
} >> "$OUT"

echo "release notes: CHANGELOG.md section ${VERSION}"
