#!/usr/bin/env bash
# =============================================================================
# publish-nuget.sh
# Packs every NuGet package under src/*/*.csproj|*.fsproj of this repository and
# uploads them to the repository owner's GitHub Packages NuGet registry. Kept
# generic -> usable unchanged in any repository with the same layout
# (src/<PackageName>/<PackageName>.csproj or .fsproj); the owner is derived from
# the git remote.
#
# NOTE: this publishes to GitHub Packages, which requires a token even for read
# access. Public releases belong on nuget.org — decide the feed before using
# this for a public version.
#
# Usage:
#   chmod +x publish-nuget.sh
#   ./publish-nuget.sh
#
# Optional flags:
#   --dry-run   Pack only, do not upload
#   --version X Override the version number (e.g. --version 1.2.0)
#   --tag T     Pack the tagged commit T (e.g. --tag v1.2.0) instead of the working
#               tree: a temporary git worktree is checked out at the tag, packed and
#               removed again. Use it for releases — the working tree may already be
#               ahead of the tag (docs ship inside the packages). For a tag of the
#               form vX.Y.Z (or X.Y.Z) every package must carry version X.Y.Z, or
#               nothing is uploaded. Cannot be combined with --version.
#
# Without --tag the working tree is packed as it is; the script warns when HEAD is
# not exactly a tagged commit or has uncommitted changes.
#
# Environment variables (optional; derived automatically when unset):
#   GITHUB_OWNER – GitHub organisation or user the packages land under.
#                  Default: parsed from the "origin" remote
#                  (git@github.com:OWNER/REPO.git or https://github.com/OWNER/REPO).
#   GITHUB_TOKEN – GitHub Personal Access Token with scope "write:packages"
#                  (classic PAT) or permission "Packages: Read and write"
#                  (fine-grained PAT). Default: `gh auth token`, if the GitHub
#                  CLI is installed and logged in.
#
# Examples:
#   ./publish-nuget.sh                      # derive owner + token automatically
#   ./publish-nuget.sh --version 1.1.0
#   ./publish-nuget.sh --dry-run
#   ./publish-nuget.sh --tag v1.1.0 --dry-run   # check a release, then run it without --dry-run
#
#   # Publish under a different owner (e.g. a fork / another account):
#   GITHUB_OWNER=other-org ./publish-nuget.sh
#
# Note: for GitHub to show the package on the right repository page, the
# .csproj/.fsproj (or Directory.Build.props) needs a <RepositoryUrl> pointing at
# this repository.
# =============================================================================

set -euo pipefail

# ── Colors ────────────────────────────────────────────────────────────────────
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
NC='\033[0m' # No Color

# ── Parse arguments ───────────────────────────────────────────────────────────
DRY_RUN=false
VERSION_OVERRIDE=""
RELEASE_TAG=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --dry-run)
      DRY_RUN=true
      shift
      ;;
    --version)
      VERSION_OVERRIDE="$2"
      shift 2
      ;;
    --tag)
      RELEASE_TAG="$2"
      shift 2
      ;;
    *)
      echo -e "${RED}Unknown argument: $1${NC}"
      exit 1
      ;;
  esac
done

if [ -n "$RELEASE_TAG" ] && [ -n "$VERSION_OVERRIDE" ]; then
  echo -e "${RED}--tag and --version cannot be combined: a tag is released with the version it carries.${NC}"
  exit 1
fi

# ── Determine the GitHub owner ────────────────────────────────────────────────
# Parsed from the "origin" remote unless given as an environment variable, so the
# script works unchanged in any GitHub repository without hard-coding an owner.
# Covers both common remote formats:
#   git@github.com:OWNER/REPO.git
#   https://github.com/OWNER/REPO(.git)
if [ -z "${GITHUB_OWNER:-}" ]; then
  ORIGIN_URL=$(git remote get-url origin 2>/dev/null || true)
  GITHUB_OWNER=$(echo "${ORIGIN_URL}" | sed -nE 's#.*github\.com[:/]+([^/]+)/.*#\1#p')
fi

# ── Determine the GitHub token ────────────────────────────────────────────────
# Falls back to an already logged-in `gh` when no environment variable is set,
# which usually avoids having to create a PAT by hand.
if [ -z "${GITHUB_TOKEN:-}" ] && command -v gh >/dev/null 2>&1; then
  GITHUB_TOKEN=$(gh auth token 2>/dev/null || true)
fi

# ── Configuration ─────────────────────────────────────────────────────────────

REPO_ROOT=$(pwd)
OUTPUT_DIR="${REPO_ROOT}/nupkg"
# dotnet is a native program: on Git for Windows it would read /c/... as C:\c\...
OUTPUT_DIR_NATIVE=$(cygpath -m "${OUTPUT_DIR}" 2>/dev/null || echo "${OUTPUT_DIR}")
FEED_URL="https://nuget.pkg.github.com/${GITHUB_OWNER:-unknown}/index.json"

# ── Choose the source: a tagged commit, or the working tree ────────────────────
# A release packs the tag, not whatever happens to be checked out: the working tree
# may already be ahead of the tag, and the docs ship inside the packages.
WORKTREE_DIR=""
EXPECTED_VERSION=""
if [ -n "$RELEASE_TAG" ]; then
  if ! git rev-parse -q --verify "refs/tags/${RELEASE_TAG}^{commit}" >/dev/null; then
    echo -e "${RED}Tag '${RELEASE_TAG}' not found (git tag --list shows the local tags; git fetch --tags fetches them).${NC}"
    exit 1
  fi

  WORKTREE_DIR=$(mktemp -d "${TMPDIR:-/tmp}/publish-nuget.XXXXXX")
  # Git for Windows needs a native path; elsewhere cygpath does not exist.
  WORKTREE_GIT_PATH=$(cygpath -m "${WORKTREE_DIR}" 2>/dev/null || echo "${WORKTREE_DIR}")

  cleanup_worktree() {
    cd "${REPO_ROOT}"
    git worktree remove --force "${WORKTREE_GIT_PATH}" >/dev/null 2>&1 || rm -rf "${WORKTREE_DIR}"
    git worktree prune >/dev/null 2>&1 || true
  }
  trap cleanup_worktree EXIT

  git worktree add --detach --quiet "${WORKTREE_GIT_PATH}" "${RELEASE_TAG}"
  cd "${WORKTREE_DIR}"

  # vX.Y.Z / X.Y.Z (optionally with a pre-release suffix) → the version every package must carry.
  if [[ "${RELEASE_TAG}" =~ ^v?([0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?)$ ]]; then
    EXPECTED_VERSION="${BASH_REMATCH[1]}"
  fi
  SOURCE_DESCRIPTION="tag ${RELEASE_TAG} ($(git rev-parse --short HEAD))"
else
  SOURCE_DESCRIPTION="working tree ($(git rev-parse --short HEAD 2>/dev/null || echo 'no commit'))"
fi

# Display name for the banner: name of the solution file (*.sln/*.slnx) in the
# repository root, otherwise the name of the current directory.
SOLUTION_FILE=$(find . -maxdepth 1 \( -name "*.sln" -o -name "*.slnx" \) | head -n1)
if [ -n "$SOLUTION_FILE" ]; then
  REPO_NAME=$(basename "$SOLUTION_FILE")
  REPO_NAME="${REPO_NAME%.*}"
else
  REPO_NAME=$(basename "${REPO_ROOT}")
fi

# Discover every project to pack: each project under src/<Name>/<Name>.csproj|.fsproj
# that does not explicitly set IsPackable=false. .fsproj is included so F# packages
# (e.g. a .FSharp facade next to the C# core) are picked up as automatically as .csproj.
#
# Sorted by the length of the project folder name (then alphabetically): with the usual
# naming convention (core first, then core.adapter — e.g. ImagePro.Db before
# ImagePro.Db.MySql) this puts the "core" ahead of its adapters. (Sorting the full paths
# would sort wrongly, because '.' precedes '/' in ASCII, so "ImagePro.Db.MySql/..." would
# come before "ImagePro.Db/...".)
PROJECTS=()
while IFS= read -r PROJ; do
  [ -z "$PROJ" ] && continue
  if ! grep -qi "<IsPackable>false</IsPackable>" "$PROJ"; then
    PROJECTS+=("$PROJ")
  fi
done < <(
  find src -mindepth 2 -maxdepth 2 \( -name "*.csproj" -o -name "*.fsproj" \) |
  while IFS= read -r PROJ; do
    NAME=$(basename "$(dirname "$PROJ")")
    printf '%03d\t%s\t%s\n' "${#NAME}" "$NAME" "$PROJ"
  done | sort | cut -f3-
)

if [ "${#PROJECTS[@]}" -eq 0 ]; then
  echo -e "${RED}No packable projects found under src/*/*.csproj|*.fsproj.${NC}"
  exit 1
fi

# ── Check required environment variables (skipped on a dry run) ───────────────
if [ "$DRY_RUN" = false ]; then
  : "${GITHUB_OWNER:?Could not derive the GitHub owner from the 'origin' remote. Please set GITHUB_OWNER, e.g. export GITHUB_OWNER=my-org}"
  : "${GITHUB_TOKEN:?Please set GITHUB_TOKEN (PAT with scope write:packages) or log in with 'gh auth login'}"
fi

# ── Prepare the output directory ──────────────────────────────────────────────
rm -rf "${OUTPUT_DIR}"
mkdir -p "${OUTPUT_DIR}"

# Draws a box with a centred title; the width follows the title length.
print_box() {
  local title="$1"
  local inner_width=$((${#title} + 4))
  local border
  border=$(printf '═%.0s' $(seq 1 "$inner_width"))
  echo -e "${CYAN}╔${border}╗${NC}"
  printf "${CYAN}║  %s  ║${NC}\n" "$title"
  echo -e "${CYAN}╚${border}╝${NC}"
}

echo ""
print_box "${REPO_NAME} – NuGet Publish"
echo ""
echo -e "  Source: ${SOURCE_DESCRIPTION}"
echo ""

if [ "$DRY_RUN" = true ]; then
  echo -e "${YELLOW}  ⚠  DRY RUN – packages are built but NOT uploaded${NC}"
  echo ""
fi

if [ -n "$VERSION_OVERRIDE" ]; then
  echo -e "${YELLOW}  ⚠  Version override: ${VERSION_OVERRIDE}${NC}"
  echo ""
fi

# Packing the working tree for a release is easy to get wrong: say so.
if [ -z "$RELEASE_TAG" ]; then
  if ! git describe --exact-match --tags HEAD >/dev/null 2>&1; then
    echo -e "${YELLOW}  ⚠  HEAD is not a tagged commit — for a release, use --tag <tag>${NC}"
    echo ""
  fi
  if [ -n "$(git status --porcelain 2>/dev/null)" ]; then
    echo -e "${YELLOW}  ⚠  Uncommitted changes in the working tree are packed too${NC}"
    echo ""
  fi
fi

# ── Step 1: pack everything ───────────────────────────────────────────────────
echo -e "${CYAN}==> Step 1: packing${NC}"
echo ""

PACK_ARGS=(
  "--configuration" "Release"
  "--output" "${OUTPUT_DIR_NATIVE}"
)

# Override the version number when one was given
if [ -n "$VERSION_OVERRIDE" ]; then
  PACK_ARGS+=("-p:Version=${VERSION_OVERRIDE}")
fi

for PROJECT in "${PROJECTS[@]}"; do
  PACKAGE_NAME=$(basename "$(dirname "${PROJECT}")")
  echo -e "  ${CYAN}▶ Packing ${PACKAGE_NAME}…${NC}"
  dotnet pack "${PROJECT}" "${PACK_ARGS[@]}"
  echo -e "  ${GREEN}✓ ${PACKAGE_NAME} packed${NC}"
  echo ""
done

# ── Show the resulting packages ───────────────────────────────────────────────
if ! compgen -G "${OUTPUT_DIR}/*.nupkg" >/dev/null; then
  echo -e "${RED}No packages found in ${OUTPUT_DIR} after packing — nothing to upload.${NC}"
  exit 1
fi

echo -e "${CYAN}==> Packages produced:${NC}"
echo ""
for NUPKG in "${OUTPUT_DIR}"/*.nupkg; do
  SIZE=$(du -sh "${NUPKG}" | cut -f1)
  echo -e "  ${GREEN}✓${NC} $(basename "${NUPKG}") (${SIZE})"
done
echo ""

# ── A tag's packages must carry the tag's version ─────────────────────────────
# Catches a tag whose version bump was forgotten — before anything is uploaded.
if [ -n "$EXPECTED_VERSION" ]; then
  MISMATCHED=0
  for NUPKG in "${OUTPUT_DIR}"/*.nupkg; do
    case "$(basename "${NUPKG}")" in
      *".${EXPECTED_VERSION}.nupkg") ;;
      *)
        echo -e "  ${RED}✗ $(basename "${NUPKG}") does not carry version ${EXPECTED_VERSION} (tag ${RELEASE_TAG})${NC}"
        MISMATCHED=$((MISMATCHED + 1))
        ;;
    esac
  done
  if [ "$MISMATCHED" -gt 0 ]; then
    echo ""
    echo -e "${RED}Version check failed — nothing uploaded. Is the version bump part of tag ${RELEASE_TAG}?${NC}"
    exit 1
  fi
  echo -e "  ${GREEN}✓ All packages carry version ${EXPECTED_VERSION} (tag ${RELEASE_TAG})${NC}"
  echo ""
fi

# ── Step 2: upload ────────────────────────────────────────────────────────────
if [ "$DRY_RUN" = true ]; then
  echo -e "${YELLOW}==> Dry run: upload skipped.${NC}"
  echo ""
  echo -e "  To upload, run:"
  if [ -n "$RELEASE_TAG" ]; then
    echo -e "  ${CYAN}./publish-nuget.sh --tag ${RELEASE_TAG}${NC}"
  else
    echo -e "  ${CYAN}./publish-nuget.sh${NC}"
  fi
  echo ""
  exit 0
fi

echo -e "${CYAN}==> Step 2: uploading to GitHub Packages${NC}"
echo -e "    Owner: ${GITHUB_OWNER}"
echo -e "    Feed:  ${FEED_URL}"
echo ""

UPLOAD_COUNT=0
SKIP_COUNT=0
FAIL_COUNT=0

for NUPKG in "${OUTPUT_DIR}"/*.nupkg; do
  FILENAME=$(basename "${NUPKG}")
  echo -e "  ${CYAN}▶ Uploading: ${FILENAME}${NC}"

  # Do NOT discard the exit code (no "|| true") -> real failures (401/403/404/network/…)
  # must be recognised as failures instead of being waved through as success.
  set +e
  OUTPUT=$(dotnet nuget push "${NUPKG}" \
    --source "${FEED_URL}" \
    --api-key "${GITHUB_TOKEN}" \
    --skip-duplicate 2>&1)
  PUSH_EXIT_CODE=$?
  set -e

  if echo "${OUTPUT}" | grep -qi "already exists\|conflict\|409"; then
    echo -e "  ${YELLOW}⚠  ${FILENAME} – already present, skipped${NC}"
    SKIP_COUNT=$((SKIP_COUNT + 1))
  elif [ "$PUSH_EXIT_CODE" -eq 0 ]; then
    echo -e "  ${GREEN}✓  ${FILENAME} – uploaded${NC}"
    UPLOAD_COUNT=$((UPLOAD_COUNT + 1))
  else
    echo -e "  ${RED}✗  ${FILENAME} – upload failed (exit code ${PUSH_EXIT_CODE})${NC}"
    echo -e "${RED}${OUTPUT}${NC}" | sed 's/^/    /'
    FAIL_COUNT=$((FAIL_COUNT + 1))
  fi
  echo ""
done

# ── Summary ───────────────────────────────────────────────────────────────────
if [ "$FAIL_COUNT" -eq 0 ]; then
  echo -e "${GREEN}╔══════════════════════════════════════════════════════════════╗${NC}"
  echo -e "${GREEN}║  Done!                                                       ║${NC}"
  echo -e "${GREEN}╚══════════════════════════════════════════════════════════════╝${NC}"
else
  echo -e "${RED}╔══════════════════════════════════════════════════════════════╗${NC}"
  echo -e "${RED}║  Done, with errors!                                          ║${NC}"
  echo -e "${RED}╚══════════════════════════════════════════════════════════════╝${NC}"
fi
echo ""
echo -e "  Uploaded : ${GREEN}${UPLOAD_COUNT}${NC} package(s)"
echo -e "  Skipped  : ${YELLOW}${SKIP_COUNT}${NC} package(s) (already present)"
echo -e "  Failed   : ${RED}${FAIL_COUNT}${NC} package(s)"
echo ""
echo -e "  Packages available at:"
echo -e "  ${CYAN}https://github.com/${GITHUB_OWNER}?tab=packages${NC}"
echo ""
echo -e "  Consuming them from nuget.config (read access also needs a token with"
echo -e "  scope read:packages — GitHub Packages allows no anonymous reads):"
echo -e "  ${CYAN}<packageSources>"
echo -e "    <add key=\"github\" value=\"${FEED_URL}\" />"
echo -e "  </packageSources>"
echo -e "  <packageSourceCredentials>"
echo -e "    <github>"
echo -e "      <add key=\"Username\" value=\"${GITHUB_OWNER}\" />"
echo -e "      <add key=\"ClearTextPassword\" value=\"%GITHUB_TOKEN%\" />"
echo -e "    </github>"
echo -e "  </packageSourceCredentials>${NC}"
echo ""

if [ "$FAIL_COUNT" -gt 0 ]; then
  exit 1
fi
