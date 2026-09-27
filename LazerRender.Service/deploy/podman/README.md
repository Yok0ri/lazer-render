# Podman deployment

LazerRender ships **one OCI image** that both Docker and Podman consume — there is no separate
"Podman image". What lives here is the Podman-native way to run it: [quadlet](https://docs.podman.io/en/latest/markdown/podman-systemd.unit.5.html)
units for rootless systemd, plus notes for `podman compose` and plain `podman run`.

The image is `ghcr.io/yok0ri/lazer-render` (`linux/amd64`), tagged `X.Y.Z`, `X.Y` and `latest`.
Pick one of the three options below.

---

## Option A — quadlet (recommended for a always-on home server)

Install the three units for your user (rootless):

```bash
mkdir -p ~/.config/containers/systemd ~/.config/lazerrender
cp LazerRender.Service/deploy/podman/lazerrender.container \
   LazerRender.Service/deploy/podman/lazerrender-*.volume \
   ~/.config/containers/systemd/

# Environment, using the app's setting names (ASP.NET's double-underscore form).
# NOTE: a quadlet EnvironmentFile is passed through unchanged — unlike docker-compose.yml,
# there is no short-alias mapping here, so OSU_CLIENT_ID etc. would NOT be picked up.
cat > ~/.config/lazerrender/.env <<'EOF'
Osu__OAuth__ClientId=...
Osu__OAuth__ClientSecret=...
Osu__OAuth__RedirectUri=https://your.domain/auth/callback
AllowedHosts=your.domain
Admin__OsuUserIds=1234567
# Optional: only needed if your reverse proxy is not on 127.0.0.1 / the podman network.
# Proxy__KnownProxies=...
# Proxy__KnownNetworks=...
# Optional fallback osu! user credential:
# Renderer__OsuBotRefreshToken=...
EOF
chmod 600 ~/.config/lazerrender/.env

systemctl --user daemon-reload        # quadlet units are generated here — there is no `enable`
systemctl --user start lazerrender
systemctl --user status lazerrender

# After a reboot / login, start automatically:
loginctl enable-linger "$USER"
```

The stack serves on **host port 5180** (`http://localhost:5180`). `podman auto-update` (run it from a
user timer, or manually) pulls a newer image when `AutoUpdate=registry` and the `Image=` tag moves.

## Option B — podman compose

`podman compose` (Podman ≥ 4.7) or `podman-compose` reads the repository's `docker-compose.yml`
as-is; only the `dockerfile:` key had to change (it points at `Containerfile`):

```bash
cd lazer-render
podman compose up -d --build      # or: podman-compose up -d --build
curl -fsS http://127.0.0.1:5180/health
```

## Option C — plain podman run

```bash
podman run -d --name lazerrender \
  -p 5180:5080 \
  --device /dev/dri/renderD128 \
  --shm-size=1g \
  --init \
  -v lazerrender-data:/app/data \
  -v lazerrender-keys:/app/keys \
  --env-file .env \
  ghcr.io/yok0ri/lazer-render:latest
```

`--init` reaps the engine/weston/ffmpeg grandchildren (the compose stack's `init: true`).

---

## Building the image locally

The build file is named for Podman; Docker follows the committed `Dockerfile` symlink to it.

```bash
podman build -f Containerfile -t lazerrender:local .
# or, from the compose file:
podman compose build
```

Build context is the repository root and **must** include the pinned osu! submodule
(`git submodule update --init --recursive`).

## GPU passthrough

Only pass the render node of the card you want to render on — Mesa enumerates every node it can see
and does not necessarily pick the discrete GPU. On the host:

```bash
ls -l /dev/dri/by-path/
cat /sys/class/drm/renderD128/device/uevent      # PCI_ID / PCI_SLOT_NAME
```

Then set the matching `AddDevice=` in the quadlet unit (or `--device` / the compose `devices:` line).
The nodes are world-readable/writable (`crw-rw-rw-`), so rootless Podman needs no extra groups.

## SELinux

Named Podman volumes (`lazerrender-data`, `lazerrender-keys`) are labelled automatically. Only **bind
mounts** need a label suffix: `-v /host/path:/app/data:Z` (private) or `:z` (shared). Do not bind-mount
`/app/keys` at all — keep the key ring in a named volume.

## Rootless notes

- cgroup v2 (the default on Fedora and Ubuntu 24.04) is required for `ShmSize` and resource limits.
- If `podman` reports a storage problem, `podman system migrate` (or `podman unshare`) usually repairs
  the rootless store.
- `--group-add keep-groups` is only needed if your device access relies on a host group; the standard
  render nodes do not.

## Verifying a deployment

```bash
podman ps                                   # lazerrender Up (healthy)
podman exec lazerrender curl -fsS http://127.0.0.1:5080/health
podman exec lazerrender ffmpeg -version | head -1
podman exec lazerrender ls /dev/dri         # renderD128 visible
```

Then open `http://localhost:5180`, sign in with osu!, and queue a short render to confirm the GPU path.

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| `status` shows `activating` forever | image pull is slow on first start (large); check `podman logs lazerrender` |
| render fails, no GPU | wrong `AddDevice=` node, or the node was not passed at all |
| 4K/1440p render fails | `ShmSize` too small (needs `1g`) |
| healthcheck unhealthy | app failed to start — usually a missing required env var (`Osu__OAuth__RedirectUri` once `ClientId` is set) |
| cookie not `Secure` behind a proxy | proxy address not listed in `KNOWN_PROXIES`/`KNOWN_NETWORKS` |
