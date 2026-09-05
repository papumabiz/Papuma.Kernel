#!/usr/bin/env bash
# =============================================================================
# publish-nuget.sh
# Baut alle NuGet-Pakete unter src/*/*.csproj|*.fsproj dieses Repos und lädt
# sie in die GitHub-Packages-NuGet-Registry des Repo-Owners hoch. Generisch
# gehalten -> unverändert in jedem Repo mit gleichem Layout
# (src/<PackageName>/<PackageName>.csproj bzw. .fsproj) nutzbar, Owner/Repo
# werden automatisch aus dem Git-Remote ermittelt.
#
# Verwendung:
#   chmod +x publish-nuget.sh
#   ./publish-nuget.sh
#
# Optionale Flags:
#   --dry-run   Nur bauen, nicht hochladen
#   --version X Versionsnummer überschreiben (z. B. --version 1.2.0)
#
# Umgebungsvariablen (optional; ohne sie wird automatisch ermittelt/nachgefragt):
#   GITHUB_OWNER – GitHub-Organisation oder -Benutzer, unter dem die Pakete
#                  landen. Default: aus dem "origin"-Remote geparst
#                  (git@github.com:OWNER/REPO.git bzw. https://github.com/OWNER/REPO).
#   GITHUB_TOKEN – GitHub Personal Access Token mit Scope "write:packages"
#                  (classic PAT) bzw. Berechtigung "Packages: Read and write"
#                  (fine-grained PAT). Default: `gh auth token`, falls die
#                  GitHub-CLI installiert und eingeloggt ist.
#
# Beispiel:
#   ./publish-nuget.sh                      # Owner + Token automatisch ermitteln
#   ./publish-nuget.sh --version 1.1.0
#   ./publish-nuget.sh --dry-run
#
#   # Gegen einen anderen Owner veröffentlichen (z. B. Fork/anderes Konto):
#   GITHUB_OWNER=other-org ./publish-nuget.sh
#
# Hinweis: damit GitHub das Paket auf der richtigen Repo-Seite anzeigt, sollte
# das/die .csproj/.fsproj (oder Directory.Build.props) ein <RepositoryUrl>
# tragen, das auf dieses Repo zeigt.
# =============================================================================

set -euo pipefail

# ── Farben ────────────────────────────────────────────────────────────────────
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
NC='\033[0m' # No Color

# ── Argumente parsen ──────────────────────────────────────────────────────────
DRY_RUN=false
VERSION_OVERRIDE=""

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
    *)
      echo -e "${RED}Unbekanntes Argument: $1${NC}"
      exit 1
      ;;
  esac
done

# ── GitHub Owner ermitteln ────────────────────────────────────────────────────
# Aus dem "origin"-Remote geparst, falls nicht per Env-Var vorgegeben – so
# funktioniert das Script unverändert in jedem GitHub-Repo, ohne einen Owner
# hart zu kodieren. Deckt beide üblichen Remote-Formate ab:
#   git@github.com:OWNER/REPO.git
#   https://github.com/OWNER/REPO(.git)
if [ -z "${GITHUB_OWNER:-}" ]; then
  ORIGIN_URL=$(git remote get-url origin 2>/dev/null || true)
  GITHUB_OWNER=$(echo "${ORIGIN_URL}" | sed -nE 's#.*github\.com[:/]+([^/]+)/.*#\1#p')
fi

# ── GitHub Token ermitteln ────────────────────────────────────────────────────
# Fällt auf ein bereits eingeloggtes `gh` zurück, falls nicht per Env-Var
# vorgegeben – erspart das manuelle Anlegen eines PATs im Regelfall.
if [ -z "${GITHUB_TOKEN:-}" ] && command -v gh >/dev/null 2>&1; then
  GITHUB_TOKEN=$(gh auth token 2>/dev/null || true)
fi

# ── Konfiguration ─────────────────────────────────────────────────────────────

OUTPUT_DIR="./nupkg"
FEED_URL="https://nuget.pkg.github.com/${GITHUB_OWNER:-unknown}/index.json"

# Anzeigename fürs Banner: Name der Solution-Datei (*.sln/*.slnx) im Repo-Root,
# sonst Name des aktuellen Verzeichnisses.
SOLUTION_FILE=$(find . -maxdepth 1 \( -name "*.sln" -o -name "*.slnx" \) | head -n1)
if [ -n "$SOLUTION_FILE" ]; then
  REPO_NAME=$(basename "$SOLUTION_FILE")
  REPO_NAME="${REPO_NAME%.*}"
else
  REPO_NAME=$(basename "$(pwd)")
fi

# Alle zu bauenden Projekte automatisch ermitteln: jedes Projekt unter
# src/<Name>/<Name>.csproj|.fsproj, das nicht explizit IsPackable=false gesetzt
# hat. .fsproj mit rein, damit F#-Pakete (z. B. eine .FSharp-Fassade neben dem
# C#-Kern) genauso automatisch erfasst werden wie .csproj.
#
# Sortierung nach Länge des Projektordnernamens (dann alphabetisch): bei der
# üblichen Namenskonvention (Core zuerst, dann Core.Adapter, z. B. ImagePro.Db
# vor ImagePro.Db.MySql) kommt so automatisch der "Core" vor den Adaptern.
# (Reines Sortieren der vollen Pfade würde hier falsch sortieren, weil '.' im
# ASCII vor '/' kommt -> "ImagePro.Db.MySql/..." käme vor "ImagePro.Db/...".)
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
  echo -e "${RED}Keine packbaren Projekte unter src/*/*.csproj|*.fsproj gefunden.${NC}"
  exit 1
fi

# ── Pflicht-Umgebungsvariablen prüfen (nur wenn kein Dry-Run) ─────────────────
if [ "$DRY_RUN" = false ]; then
  : "${GITHUB_OWNER:?Konnte den GitHub-Owner nicht aus dem 'origin'-Remote ermitteln. Bitte GITHUB_OWNER setzen, z. B. export GITHUB_OWNER=my-org}"
  : "${GITHUB_TOKEN:?Bitte GITHUB_TOKEN setzen (PAT mit Scope write:packages) oder mit 'gh auth login' einloggen}"
fi

# ── Ausgabe-Verzeichnis vorbereiten ───────────────────────────────────────────
rm -rf "${OUTPUT_DIR}"
mkdir -p "${OUTPUT_DIR}"

# Zeichnet eine Box mit zentriertem Titel, Breite passt sich der Titellänge an.
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

if [ "$DRY_RUN" = true ]; then
  echo -e "${YELLOW}  ⚠  DRY-RUN – Pakete werden gebaut, aber NICHT hochgeladen${NC}"
  echo ""
fi

if [ -n "$VERSION_OVERRIDE" ]; then
  echo -e "${YELLOW}  ⚠  Versionsüberschreibung: ${VERSION_OVERRIDE}${NC}"
  echo ""
fi

# ── Schritt 1: Alle Pakete bauen ──────────────────────────────────────────────
echo -e "${CYAN}==> Schritt 1: Pakete bauen${NC}"
echo ""

PACK_ARGS=(
  "--configuration" "Release"
  "--output" "${OUTPUT_DIR}"
)

# Versionsnummer überschreiben, falls angegeben
if [ -n "$VERSION_OVERRIDE" ]; then
  PACK_ARGS+=("-p:Version=${VERSION_OVERRIDE}")
fi

for PROJECT in "${PROJECTS[@]}"; do
  PACKAGE_NAME=$(basename "$(dirname "${PROJECT}")")
  echo -e "  ${CYAN}▶ Baue ${PACKAGE_NAME}…${NC}"
  dotnet pack "${PROJECT}" "${PACK_ARGS[@]}"
  echo -e "  ${GREEN}✓ ${PACKAGE_NAME} erfolgreich gebaut${NC}"
  echo ""
done

# ── Erzeugte Pakete anzeigen ──────────────────────────────────────────────────
echo -e "${CYAN}==> Erzeugte Pakete:${NC}"
echo ""
for NUPKG in "${OUTPUT_DIR}"/*.nupkg; do
  SIZE=$(du -sh "${NUPKG}" | cut -f1)
  echo -e "  ${GREEN}✓${NC} $(basename "${NUPKG}") (${SIZE})"
done
echo ""

# ── Schritt 2: Pakete hochladen ───────────────────────────────────────────────
if [ "$DRY_RUN" = true ]; then
  echo -e "${YELLOW}==> Dry-Run: Upload übersprungen.${NC}"
  echo ""
  echo -e "  Zum Hochladen ausführen:"
  echo -e "  ${CYAN}./publish-nuget.sh${NC}"
  echo ""
  exit 0
fi

echo -e "${CYAN}==> Schritt 2: Pakete in GitHub Packages hochladen${NC}"
echo -e "    Owner: ${GITHUB_OWNER}"
echo -e "    Feed:  ${FEED_URL}"
echo ""

UPLOAD_COUNT=0
SKIP_COUNT=0
FAIL_COUNT=0

for NUPKG in "${OUTPUT_DIR}"/*.nupkg; do
  FILENAME=$(basename "${NUPKG}")
  echo -e "  ${CYAN}▶ Uploading: ${FILENAME}${NC}"

  # Exit-Code NICHT wegwerfen (kein "|| true") -> echte Fehler (401/403/404/Netzwerk/…)
  # müssen als Fehler erkannt werden, nicht als Erfolg durchgewunken werden.
  set +e
  OUTPUT=$(dotnet nuget push "${NUPKG}" \
    --source "${FEED_URL}" \
    --api-key "${GITHUB_TOKEN}" \
    --skip-duplicate 2>&1)
  PUSH_EXIT_CODE=$?
  set -e

  if echo "${OUTPUT}" | grep -qi "already exists\|conflict\|409"; then
    echo -e "  ${YELLOW}⚠  ${FILENAME} – bereits vorhanden, übersprungen${NC}"
    SKIP_COUNT=$((SKIP_COUNT + 1))
  elif [ "$PUSH_EXIT_CODE" -eq 0 ]; then
    echo -e "  ${GREEN}✓  ${FILENAME} – erfolgreich hochgeladen${NC}"
    UPLOAD_COUNT=$((UPLOAD_COUNT + 1))
  else
    echo -e "  ${RED}✗  ${FILENAME} – Upload fehlgeschlagen (Exit-Code ${PUSH_EXIT_CODE})${NC}"
    echo -e "${RED}${OUTPUT}${NC}" | sed 's/^/    /'
    FAIL_COUNT=$((FAIL_COUNT + 1))
  fi
  echo ""
done

# ── Zusammenfassung ───────────────────────────────────────────────────────────
if [ "$FAIL_COUNT" -eq 0 ]; then
  echo -e "${GREEN}╔══════════════════════════════════════════════════════════════╗${NC}"
  echo -e "${GREEN}║  Fertig!                                                     ║${NC}"
  echo -e "${GREEN}╚══════════════════════════════════════════════════════════════╝${NC}"
else
  echo -e "${RED}╔══════════════════════════════════════════════════════════════╗${NC}"
  echo -e "${RED}║  Fertig mit Fehlern!                                         ║${NC}"
  echo -e "${RED}╚══════════════════════════════════════════════════════════════╝${NC}"
fi
echo ""
echo -e "  Hochgeladen : ${GREEN}${UPLOAD_COUNT}${NC} Paket(e)"
echo -e "  Übersprungen: ${YELLOW}${SKIP_COUNT}${NC} Paket(e) (bereits vorhanden)"
echo -e "  Fehlgeschlagen: ${RED}${FAIL_COUNT}${NC} Paket(e)"
echo ""
echo -e "  Pakete verfügbar unter:"
echo -e "  ${CYAN}https://github.com/${GITHUB_OWNER}?tab=packages${NC}"
echo ""
echo -e "  Einbinden in nuget.config (Lesezugriff braucht ebenfalls ein Token"
echo -e "  mit Scope read:packages, GitHub Packages erlaubt keine anonymen Reads):"
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
