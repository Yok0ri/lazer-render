# syntax=docker/dockerfile:1
#
# LazerRender — one image containing both halves.
#
# The engine is built here and shipped as a *published*, framework-dependent .NET application. That is
# the important design decision: the render host then needs only the .NET runtime, not the SDK and not
# the source tree (which includes the pinned osu! submodule). It also means run-headless.sh no longer
# has to `dotnet run` — see LAZERRENDER_ENGINE below.
#
# Build with the repository root as the context:
#   docker build -t lazerrender:latest .
# or just `docker compose up -d --build`.
#
# The image is linux/amd64 only, and the container needs GPU access at run time: pass the render node(s)
# of the card you want to render on (`--device /dev/dri/renderD128`). See DEPLOYMENT.md §11.

# ---------- build ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# The context includes LazerRender.Game/extern/osu (the pinned submodule) because the engine cannot be
# built without it. .dockerignore keeps build output and runtime data out.
COPY . .

# RID-specific and English-only: the default publish would copy native assets for ~30 other runtimes
# (~660 MB) plus a satellite assembly per culture.
ARG PUBLISH_ARGS="-c Release -r linux-x64 --self-contained false -p:SatelliteResourceLanguages=en"

RUN dotnet publish LazerRender.Game/LazerRender.Game.csproj $PUBLISH_ARGS -o /out/engine
RUN dotnet publish LazerRender.Service/src/LazerRender.Api/LazerRender.Api.csproj $PUBLISH_ARGS -o /out/api

# ---------- runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# ffmpeg CLI      — the engine pipes raw frames to it and the service probes it for the encoder backend.
# weston + Mesa   — run-headless.sh stands up a headless compositor so the engine gets a real,
#                   GPU-backed EGL context. The engine bundles its own SDL2 and FFmpeg libraries, so
#                   these are the system pieces it does not supply itself.
# Mesa Vulkan     — only needed if an operator forces the Zink driver (LAZERRENDER_MESA_DRIVER=zink)
#                   as a fallback for GPUs the container's radeonsi cannot drive; it costs ~10 MB.
# Mesa VA-API     — provides the *_drv_video.so state trackers (radeonsi_drv_video.so, etc.) that
#                   ffmpeg's h264_vaapi and the service's encoder probe load. The GL packages above do
#                   NOT include them, so without this the probe fails and every render falls back to
#                   libx264 (the service reports "Encoder: CPU (auto-detected)").
# curl            — container healthcheck against /health.
#
# .NET 10's Linux images are Ubuntu 24.04 (noble), not Debian bookworm. That changes two things from
# the original container work: noble ships Weston 13, which already understands
# `--backend=headless --renderer=gl` (the runner probes for the spelling), and `libasound2` is now
# `libasound2t64`. The earlier bookworm-backports workaround for Mesa 22.3/Weston 10 is therefore
# obsolete and removed. Mesa is the distro's build here; noble-updates currently ships Mesa 25.x (the
# version the bookworm image needed for RDNA4/gfx1200), so a current pull should cover modern GPUs — but
# re-check DEPLOYMENT.md §11 before relying on it for a very new part.
RUN apt-get update \
 && apt-get install -y --no-install-recommends \
      ca-certificates \
      ffmpeg \
      libegl1 \
      libgles2 \
      libdrm2 \
      libwayland-client0 \
      libwayland-server0 \
      libxkbcommon0 \
      libasound2t64 \
      curl \
      weston \
      libgl1-mesa-dri \
      libegl-mesa0 \
      libglx-mesa0 \
      libgbm1 \
      mesa-vulkan-drivers \
      mesa-va-drivers \
 && rm -rf /var/lib/apt/lists/*

RUN useradd --system --uid 10001 --create-home --shell /usr/sbin/nologin lazerrender

WORKDIR /app

# The service is published to the content root; the engine goes beside it so that run-headless.sh sits
# *inside* the content root, which is what RendererProcessRunner requires outside Development (it
# refuses to search parent directories for the script in production).
COPY --from=build /out/api/ ./
COPY --from=build /out/engine/ ./LazerRender.Game/publish/
COPY LazerRender.Game/scripts/run-headless.sh ./LazerRender.Game/scripts/run-headless.sh

# Runtime data and the Data Protection key ring are volumes, never image layers: the key ring is the
# master key for every stored osu! credential.
RUN chmod +x /app/LazerRender.Game/scripts/run-headless.sh \
 && mkdir -p /app/data /app/keys \
 && chmod 700 /app/keys \
 && chown -R lazerrender:lazerrender /app

USER lazerrender

ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://0.0.0.0:5080 \
    ASPNETCORE_CONTENTROOT=/app \
    LAZERRENDER_ENGINE=/app/LazerRender.Game/publish/LazerRender.dll

VOLUME ["/app/data", "/app/keys"]
EXPOSE 5080

HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
  CMD curl -fsS http://127.0.0.1:5080/health || exit 1

ENTRYPOINT ["/app/LazerRender.Api"]
