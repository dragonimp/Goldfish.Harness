#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SOURCE_DIR="$ROOT_DIR/src/Goldfish.Acp"
MANIFEST_PATH="$ROOT_DIR/src/Goldfish.Acp.snapshot.json"

[[ -d "$SOURCE_DIR" && -f "$MANIFEST_PATH" ]] || {
  echo "Goldfish.Acp snapshot or manifest is missing." >&2
  exit 1
}

ACTUAL_HASH="$(
  cd "$SOURCE_DIR"
  find . -type f ! -path './bin/*' ! -path './obj/*' -print0 \
    | LC_ALL=C sort -z \
    | xargs -0 shasum -a 256
)"
ACTUAL_HASH="$(printf '%s\n' "$ACTUAL_HASH" | shasum -a 256 | awk '{print $1}')"
EXPECTED_HASH="$(sed -nE 's/.*"sourceTreeSha256"[[:space:]]*:[[:space:]]*"([^"]+)".*/\1/p' "$MANIFEST_PATH")"

[[ -n "$EXPECTED_HASH" && "$ACTUAL_HASH" == "$EXPECTED_HASH" ]] || {
  echo "Goldfish.Acp snapshot hash mismatch: expected=$EXPECTED_HASH actual=$ACTUAL_HASH" >&2
  exit 1
}

echo "Goldfish.Acp snapshot verified: $ACTUAL_HASH"
