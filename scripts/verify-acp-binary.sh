#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
HOST_PROJECT="$ROOT_DIR/src/Goldfish.Harness.AcpHost/Goldfish.Harness.AcpHost.csproj"
ORBIT_ROOT="${ORBIT_ROOT:-$ROOT_DIR/../AgentFree}"
ORBIT_ACP_PROJECT="$ORBIT_ROOT/src/Orbit.Acp/Orbit.Acp.csproj"
ORBIT_ACP_DLL="$ORBIT_ROOT/src/Orbit.Acp/bin/Release/net10.0/Goldfish.Acp.dll"

[[ ! -d "$ROOT_DIR/src/Goldfish.Acp" ]] || {
  echo "Goldfish.Acp source must not be maintained in Goldfish.Harness." >&2
  exit 1
}
[[ -f "$ORBIT_ACP_PROJECT" ]] || {
  echo "Orbit public ACP project is unavailable: $ORBIT_ACP_PROJECT" >&2
  exit 1
}
dotnet build "$ORBIT_ACP_PROJECT" -c Release >/dev/null
[[ -f "$ORBIT_ACP_DLL" ]] || {
  echo "Orbit public ACP DLL is missing: $ORBIT_ACP_DLL" >&2
  exit 1
}
grep -F '<HintPath>$(OrbitAcpDll)</HintPath>' "$HOST_PROJECT" >/dev/null || {
  echo "AcpHost must reference the Orbit public Goldfish.Acp DLL." >&2
  exit 1
}
if grep -F 'ProjectReference Include="../Goldfish.Acp/' "$HOST_PROJECT" >/dev/null; then
  echo "AcpHost must not use a Goldfish.Acp ProjectReference." >&2
  exit 1
fi

ASSEMBLY_SHA="$(shasum -a 256 "$ORBIT_ACP_DLL" | awk '{print $1}')"
echo "Orbit public Goldfish.Acp DLL verified: $ASSEMBLY_SHA"
