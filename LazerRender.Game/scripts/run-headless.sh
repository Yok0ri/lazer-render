#!/usr/bin/env bash
# LazerRender headless runner.
#
# LazerRender needs a real GPU-backed EGL context (the framework's HeadlessGameHost uses a stub
# DummyRenderer, so it cannot drive FBO capture). This script stands up a throwaway Weston headless
# Wayland compositor on its own socket, runs LazerRender against it, and tears the compositor down
# afterwards — no window ever appears on the interactive desktop session.
#
# Usage (run from the repo root; paths below are relative to LazerRender.Game/):
#   LazerRender.Game/scripts/run-headless.sh --replay tests/replay_nm.osr --skin "Aristia v2" \
#       --output out --storage storage --fps 60 --width 1920 --height 1080 --encoder amd
#
# All arguments are passed through to LazerRender unchanged.

set -euo pipefail

# Resolve the runtime directory used by Wayland compositors. XDG_RUNTIME_DIR is standard; if it is
# unset (e.g. launched from a bare systemd service), fall back to a secured per-user tmpdir.
if [[ -n "${XDG_RUNTIME_DIR:-}" ]]; then
    RUNTIME_DIR="$XDG_RUNTIME_DIR"
else
    RUNTIME_DIR="/tmp/run-user-$UID"
    mkdir -p "$RUNTIME_DIR"
    chmod 700 "$RUNTIME_DIR"
fi

# Unique socket per invocation so concurrent renders never collide.
SOCKET="wayland-lazer-$$"
SOCKET_PATH="$RUNTIME_DIR/$SOCKET"

command -v weston >/dev/null 2>&1 || { echo "weston is required but not installed." >&2; exit 1; }

cleanup() {
    if [[ -n "${WESTON_PID:-}" ]] && kill -0 "$WESTON_PID" 2>/dev/null; then
        kill "$WESTON_PID" 2>/dev/null || true
        wait "$WESTON_PID" 2>/dev/null || true
    fi
    rm -f "$SOCKET_PATH" 2>/dev/null || true
}
trap cleanup EXIT INT TERM

# Force a specific Mesa driver if requested (e.g. radeonsi for AMD, iris for Intel). When unset,
# Mesa auto-selects a hardware DRI driver; --renderer=gl below prevents the software llvmpipe path.
if [[ -n "${LAZERRENDER_MESA_DRIVER:-}" ]]; then
    export MESA_LOADER_DRIVER_OVERRIDE="$LAZERRENDER_MESA_DRIVER"
fi

# Spawn a headless compositor. --renderer=gl makes Weston advertise GL-capable buffers so clients
# get a real hardware EGL context instead of falling back to llvmpipe. --no-config skips user config.
weston --backend=headless --renderer=gl --socket="$SOCKET" --no-config >/dev/null 2>&1 &
WESTON_PID=$!

# Wait for the compositor's Wayland socket to appear before launching the client.
for _ in $(seq 1 100); do
    [[ -S "$SOCKET_PATH" ]] && break
    sleep 0.05
done

if [[ ! -S "$SOCKET_PATH" ]]; then
    echo "Headless Weston socket did not appear at $SOCKET_PATH" >&2
    exit 1
fi

# Run the recorder against the headless socket.
WAYLAND_DISPLAY="$SOCKET" dotnet run --project "$(dirname "$0")/../LazerRender.Game.csproj" -- "$@"
