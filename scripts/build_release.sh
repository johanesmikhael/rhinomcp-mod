#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
PLUGIN_DIR="${REPO_ROOT}/rhino_mcp_plugin"
SERVER_DIR="${REPO_ROOT}/rhino_mcp_server"
VERSION="$(sed -n 's/^version: //p' "${PLUGIN_DIR}/manifest.yml")"
OUTPUT_DIR="${1:-${REPO_ROOT}/release/${VERSION}}"

PYTHON_VERSION="$(sed -n 's/^version = "\(.*\)"/\1/p' "${SERVER_DIR}/pyproject.toml" | head -n 1)"
PLUGIN_VERSION="$(sed -n 's/.*<Version>\(.*\)<\/Version>.*/\1/p' "${PLUGIN_DIR}/rhinomcp.csproj" | head -n 1)"

if [[ -z "${VERSION}" || "${VERSION}" != "${PYTHON_VERSION}" || "${VERSION}" != "${PLUGIN_VERSION}" ]]; then
  echo "Release versions are not synchronized: manifest=${VERSION}, python=${PYTHON_VERSION}, plugin=${PLUGIN_VERSION}" >&2
  exit 1
fi

if ! cmp -s "${REPO_ROOT}/LICENSE" "${SERVER_DIR}/LICENSE" || \
   ! cmp -s "${REPO_ROOT}/NOTICE" "${SERVER_DIR}/NOTICE"; then
  echo "Python package LICENSE/NOTICE copies differ from the repository originals." >&2
  exit 1
fi

if [[ -e "${OUTPUT_DIR}" ]]; then
  echo "Release output already exists: ${OUTPUT_DIR}" >&2
  exit 1
fi

YAK_BIN="${YAK_BIN:-}"
if [[ -z "${YAK_BIN}" ]]; then
  if command -v yak >/dev/null 2>&1; then
    YAK_BIN="$(command -v yak)"
  elif [[ -x "/Applications/Rhino 8.app/Contents/Resources/bin/yak" ]]; then
    YAK_BIN="/Applications/Rhino 8.app/Contents/Resources/bin/yak"
  else
    echo "Yak CLI not found. Set YAK_BIN to its absolute path." >&2
    exit 1
  fi
fi

PLUGIN_STAGE="${OUTPUT_DIR}/plugin-stage"
PLUGIN_FRAMEWORK_STAGE="${PLUGIN_STAGE}/net7.0"
PYTHON_OUTPUT="${OUTPUT_DIR}/python"
mkdir -p "${PLUGIN_FRAMEWORK_STAGE}" "${PYTHON_OUTPUT}"

dotnet build "${PLUGIN_DIR}/rhinomcp.sln" -c Release --no-restore -p:DeployToRhino=false

for artifact in \
  rhinomcp-mod.rhp \
  rhinomcp-mod.pdb \
  rhinomcp-mod.deps.json \
  rhinomcp-mod.runtimeconfig.json \
  Newtonsoft.Json.dll
do
  cp "${PLUGIN_DIR}/bin/Release/net7.0/${artifact}" "${PLUGIN_FRAMEWORK_STAGE}/${artifact}"
done

cp "${PLUGIN_DIR}/manifest.yml" "${PLUGIN_STAGE}/manifest.yml"
mkdir -p "${PLUGIN_STAGE}/misc"
cp "${REPO_ROOT}/README.md" "${PLUGIN_STAGE}/misc/README.md"
cp "${REPO_ROOT}/LICENSE" "${PLUGIN_STAGE}/misc/LICENSE"
cp "${REPO_ROOT}/NOTICE" "${PLUGIN_STAGE}/misc/NOTICE"

(
  cd "${PLUGIN_STAGE}"
  "${YAK_BIN}" build --platform any
)

mv "${PLUGIN_STAGE}"/*.yak "${OUTPUT_DIR}/"
(
  cd "${SERVER_DIR}"
  uv build --out-dir "${PYTHON_OUTPUT}"
)

echo "Release artifacts created in ${OUTPUT_DIR}"
