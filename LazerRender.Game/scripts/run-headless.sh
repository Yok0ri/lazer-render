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
# unset (e.g. launched from a bare systemd service or from a container), fall back to a secured
# per-user tmpdir. Note that the variable must be *exported*, not merely computed: Weston itself
# hard-fails with "environment variable XDG_RUNTIME_DIR is not set" before it opens a socket.
if [[ -n "${XDG_RUNTIME_DIR:-}" ]]; then
    RUNTIME_DIR="$XDG_RUNTIME_DIR"
else
    RUNTIME_DIR="/tmp/run-user-$UID"
    mkdir -p "$RUNTIME_DIR"
    chmod 700 "$RUNTIME_DIR"
fi
export XDG_RUNTIME_DIR="$RUNTIME_DIR"

# Unique socket per invocation so concurrent renders never collide.
SOCKET="wayland-lazer-$$"
SOCKET_PATH="$RUNTIME_DIR/$SOCKET"

command -v weston >/dev/null 2>&1 || { echo "weston is required but not installed." >&2; exit 1; }

cleanup() {
    if [[ -n "${WESTON_PID:-}" ]] && kill -0 "$WESTON_PID" 2>/dev/null; then
        kill "$WESTON_PID" 2>/dev/null || true
        wait "$WESTON_PID" 2>/dev/null || true
    fi
    rm -f "$SOCKET_PATH" "${WESTON_LOG:-}" 2>/dev/null || true
}
trap cleanup EXIT INT TERM

# Force a specific Mesa driver if requested (e.g. radeonsi for AMD, iris for Intel). When unset,
# Mesa auto-selects a hardware DRI driver; the GL renderer selected below prevents the software
# llvmpipe path.
if [[ -n "${LAZERRENDER_MESA_DRIVER:-}" ]]; then
    export MESA_LOADER_DRIVER_OVERRIDE="$LAZERRENDER_MESA_DRIVER"
fi

# Weston's command line changed between releases, so probe the installed binary instead of assuming
# a version. Weston 10 names backends by module file and only knows --use-gl; newer releases accept
# the short "headless" name and prefer --renderer=gl. The spellings picked below are valid on both
# eras, so the same script works on a desktop distro and inside the container image.
WESTON_HELP="$(weston --help 2>&1 || true)"
if grep -q 'headless-backend\.so' <<<"$WESTON_HELP"; then
    WESTON_BACKEND="headless-backend.so"
else
    WESTON_BACKEND="headless"
fi
if grep -q -- '--renderer=' <<<"$WESTON_HELP"; then
    WESTON_RENDERER=(--renderer=gl)
elif grep -q -- '--use-gl' <<<"$WESTON_HELP"; then
    WESTON_RENDERER=(--use-gl)
else
    WESTON_RENDERER=()
fi

# Spawn a headless compositor. The GL renderer makes Weston advertise GL-capable buffers so clients
# get a real hardware EGL context instead of falling back to software rasterisation. --no-config skips
# any user config. Output goes to a log so a start-up failure is diagnosable instead of silent.
WESTON_LOG="${TMPDIR:-/tmp}/weston-lazer-$$.log"
weston --backend="$WESTON_BACKEND" "${WESTON_RENDERER[@]}" --socket="$SOCKET" --no-config >"$WESTON_LOG" 2>&1 &
WESTON_PID=$!

# Wait for the compositor's Wayland socket to appear before launching the client.
for _ in $(seq 1 100); do
    [[ -S "$SOCKET_PATH" ]] && break
    sleep 0.05
done

if [[ ! -S "$SOCKET_PATH" ]]; then
    echo "Headless Weston socket did not appear at $SOCKET_PATH" >&2
    echo "--- weston output ($WESTON_LOG) ---" >&2
    cat "$WESTON_LOG" >&2 2>/dev/null || true
    exit 1
fi

# Run the recorder against the headless socket.
#
# Two modes:
#   * default — build and run from source with the SDK (`dotnet run`), which is what a development
#     checkout uses;
#   * prebuilt — when LAZERRENDER_ENGINE points at a published LazerRender.dll (how the container image
#     runs it), so the render host needs only the .NET runtime and not the SDK or the source tree.
#
# The composer is torn down by the EXIT trap either way, so the engine is deliberately run as a child
# rather than exec'd into this shell.
if [[ -n "${LAZERRENDER_ENGINE:-}" ]]; then
    if [[ ! -f "$LAZERRENDER_ENGINE" ]]; then
        echo "LAZERRENDER_ENGINE points at a missing file: $LAZERRENDER_ENGINE" >&2
        exit 1
    fi

    WAYLAND_DISPLAY="$SOCKET" dotnet "$LAZERRENDER_ENGINE" "$@"
else
    SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
    WAYLAND_DISPLAY="$SOCKET" dotnet run --project "$SCRIPT_DIR/../LazerRender.Game.csproj" -- "$@"
fi
