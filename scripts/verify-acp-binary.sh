#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ARTIFACT_DIR="$ROOT_DIR/lib/Goldfish.Acp"
MANIFEST_PATH="$ARTIFACT_DIR/snapshot.json"
HOST_PROJECT="$ROOT_DIR/src/Goldfish.Harness.AcpHost/Goldfish.Harness.AcpHost.csproj"

[[ ! -d "$ROOT_DIR/src/Goldfish.Acp" ]] || {
  echo "Goldfish.Acp source must not be maintained in Goldfish.Harness." >&2
  exit 1
}
[[ -f "$ARTIFACT_DIR/Goldfish.Acp.dll" && -f "$MANIFEST_PATH" ]] || {
  echo "Goldfish.Acp binary artifact or manifest is missing." >&2
  exit 1
}
grep -F '<HintPath>../../lib/Goldfish.Acp/Goldfish.Acp.dll</HintPath>' "$HOST_PROJECT" >/dev/null || {
  echo "AcpHost must reference the synchronized Goldfish.Acp DLL." >&2
  exit 1
}
if grep -F 'ProjectReference Include="../Goldfish.Acp/' "$HOST_PROJECT" >/dev/null; then
  echo "AcpHost must not use a Goldfish.Acp ProjectReference." >&2
  exit 1
fi

ACTUAL_ASSEMBLY_SHA="$(shasum -a 256 "$ARTIFACT_DIR/Goldfish.Acp.dll" | awk '{print $1}')"
EXPECTED_ASSEMBLY_SHA="$(sed -nE 's/.*"assemblySha256"[[:space:]]*:[[:space:]]*"([^"]+)".*/\1/p' "$MANIFEST_PATH")"
ACTUAL_ARTIFACT_HASH="$(
  (
    cd "$ARTIFACT_DIR"
    find . -type f ! -name snapshot.json -print0 \
      | LC_ALL=C sort -z \
      | xargs -0 shasum -a 256
  ) | shasum -a 256 | awk '{print $1}'
)"
EXPECTED_ARTIFACT_HASH="$(sed -nE 's/.*"artifactTreeSha256"[[:space:]]*:[[:space:]]*"([^"]+)".*/\1/p' "$MANIFEST_PATH")"

[[ -n "$EXPECTED_ASSEMBLY_SHA" && "$ACTUAL_ASSEMBLY_SHA" == "$EXPECTED_ASSEMBLY_SHA" ]] || {
  echo "Goldfish.Acp DLL hash mismatch: expected=$EXPECTED_ASSEMBLY_SHA actual=$ACTUAL_ASSEMBLY_SHA" >&2
  exit 1
}
[[ -n "$EXPECTED_ARTIFACT_HASH" && "$ACTUAL_ARTIFACT_HASH" == "$EXPECTED_ARTIFACT_HASH" ]] || {
  echo "Goldfish.Acp artifact hash mismatch: expected=$EXPECTED_ARTIFACT_HASH actual=$ACTUAL_ARTIFACT_HASH" >&2
  exit 1
}

echo "Goldfish.Acp binary verified: $ACTUAL_ASSEMBLY_SHA"
