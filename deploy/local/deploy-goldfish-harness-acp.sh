#!/usr/bin/env bash
set -euo pipefail

# Publish the ACP host independently from AgentNode. AgentNode receives only
# this stable command path and keeps its own binary/release lifecycle.
ROOT="${ROOT:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)}"
DEPLOY_ROOT="${DEPLOY_ROOT:-/Users/wengzhishan/servers/goldfish-harness-acp}"
RELEASES_DIR="$DEPLOY_ROOT/releases"
CURRENT_LINK="$DEPLOY_ROOT/current"
NODE_LABEL="${NODE_LABEL:-net.impx.goldfish-harness-agent-node}"
NODE_PLIST="${NODE_PLIST:-$HOME/Library/LaunchAgents/$NODE_LABEL.plist}"
NODE_URL="${NODE_URL:-http://127.0.0.1:8651}"
PROJECT="$ROOT/src/Goldfish.Harness.AcpHost/Goldfish.Harness.AcpHost.csproj"
DOTNET_BIN="${DOTNET_BIN:-$(command -v dotnet)}"
RELEASE_ID="$(date +%Y%m%d_%H%M%S)_$(git -C "$ROOT" rev-parse --short HEAD)"
RELEASE_DIR="$RELEASES_DIR/$RELEASE_ID"
STAGING_DIR="$RELEASES_DIR/.staging-$RELEASE_ID"

[[ -x "$DOTNET_BIN" ]] || { echo "ERROR: dotnet executable not found: $DOTNET_BIN" >&2; exit 1; }
[[ -f "$PROJECT" ]] || { echo "ERROR: ACP host project not found: $PROJECT" >&2; exit 1; }
[[ -f "$NODE_PLIST" ]] || { echo "ERROR: AgentNode plist not found: $NODE_PLIST" >&2; exit 1; }

ready_before="$(curl -fsS "$NODE_URL/readyz" 2>/dev/null || true)"
active_before="$(printf '%s' "$ready_before" | sed -nE 's/.*"active_(requests|executions)"[[:space:]]*:[[:space:]]*([0-9]+).*/\2/p' | sort -nr | head -n 1)"
if [[ -z "$active_before" ]]; then
  echo "ERROR: could not confirm AgentNode readiness at $NODE_URL/readyz" >&2
  exit 1
fi
if [[ "$active_before" != "0" ]]; then
  echo "ERROR: AgentNode has $active_before active request(s)/execution(s); retry after they finish." >&2
  exit 3
fi

mkdir -p "$RELEASES_DIR"
mkdir "$STAGING_DIR"
cleanup() { rm -rf "$STAGING_DIR"; }
trap cleanup EXIT

"$DOTNET_BIN" publish "$PROJECT" -c Release --no-restore -o "$STAGING_DIR"
[[ -x "$STAGING_DIR/Goldfish.Harness.AcpHost" ]] || {
  echo "ERROR: ACP host publish did not produce its executable" >&2
  exit 1
}

mv "$STAGING_DIR" "$RELEASE_DIR"
# BSD mv follows a destination symlink to a directory, so it can place the
# temporary link inside the old release instead of replacing `current`.
# ln -sfn replaces the symlink itself on macOS without dereferencing it.
ln -sfn "$RELEASE_DIR" "$CURRENT_LINK"

# This is configuration, not an AgentNode binary update. Future AgentNode
# deployments preserve the same key in their generated plist.
/usr/libexec/PlistBuddy -c "Delete :EnvironmentVariables:GoldfishHarnessAcp__Command" "$NODE_PLIST" >/dev/null 2>&1 || true
/usr/libexec/PlistBuddy -c "Add :EnvironmentVariables:GoldfishHarnessAcp__Command string $CURRENT_LINK/Goldfish.Harness.AcpHost" "$NODE_PLIST"
for state_setting in \
  "HarnessState__Mode:Dual" \
  "HarnessState__RetentionDays:30" \
  "HarnessState__DeltaBatchMilliseconds:50" \
  "HarnessState__DeltaBatchBytes:4096" \
  "HarnessState__LeaseSeconds:30"; do
  state_key="${state_setting%%:*}"
  state_value="${state_setting#*:}"
  /usr/libexec/PlistBuddy -c "Delete :EnvironmentVariables:$state_key" "$NODE_PLIST" >/dev/null 2>&1 || true
  /usr/libexec/PlistBuddy -c "Add :EnvironmentVariables:$state_key string $state_value" "$NODE_PLIST"
done

# launchd keeps EnvironmentVariables in its loaded job definition. Reload the
# existing service so the new command path is read; no AgentNode files change.
launchctl bootout "gui/$(id -u)/$NODE_LABEL" >/dev/null 2>&1 || true
launchctl bootout "gui/$(id -u)" "$NODE_PLIST" >/dev/null 2>&1 || true
launchctl enable "gui/$(id -u)/$NODE_LABEL"
launchctl bootstrap "gui/$(id -u)" "$NODE_PLIST"
launchctl kickstart -k "gui/$(id -u)/$NODE_LABEL"

for attempt in {1..15}; do
  ready_after="$(curl -fsS "$NODE_URL/readyz" 2>/dev/null || true)"
  if printf '%s' "$ready_after" | grep -Eq '"ok"[[:space:]]*:[[:space:]]*true'; then
    break
  fi
  sleep 2
done
if ! printf '%s' "${ready_after:-}" | grep -Eq '"ok"[[:space:]]*:[[:space:]]*true'; then
  echo "ERROR: AgentNode did not become ready after ACP host activation" >&2
  exit 1
fi

echo "goldfish_harness_release=$RELEASE_DIR"
echo "goldfish_harness_command=$CURRENT_LINK/Goldfish.Harness.AcpHost"
echo "agent_node_restarted_for_acp_host_reload=true"
