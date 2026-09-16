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
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
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
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime

# ffmpeg CLI      — the engine pipes raw frames to it and the service probes it for the encoder backend.
# weston + Mesa   — run-headless.sh stands up a headless compositor so the engine gets a real,
#                   GPU-backed EGL context. The engine bundles its own SDL2 and FFmpeg libraries, so
#                   these are the system pieces it does not supply itself.
# Mesa Vulkan     — only needed if an operator forces the Zink driver (LAZERRENDER_MESA_DRIVER=zink)
#                   as a fallback for GPUs the container's radeonsi cannot drive; it costs ~10 MB.
# curl            — container healthcheck against /health.
#
# Mesa and Weston are deliberately taken from bookworm-backports rather than the release. Bookworm
# ships Mesa 22.3 and Weston 10, both of which predate current GPU families (e.g. the AMD RDNA4 /
# gfx1200 parts): Mesa 22.3 cannot drive them at all, and Weston 10's headless backend is old enough
# that the engine's capture pipeline wedges on the first frame. The backports Mesa 25.x / Weston 14.x
# work. Only Mesa, Weston and their direct dependencies come from backports.
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates \
 && echo "deb http://deb.debian.org/debian bookworm-backports main" > /etc/apt/sources.list.d/backports.list \
 && apt-get update \
 && apt-get install -y --no-install-recommends \
      ffmpeg \
      libegl1 \
      libgles2 \
      libdrm2 \
      libwayland-client0 \
      libwayland-server0 \
      libxkbcommon0 \
      libasound2 \
      curl \
 && apt-get install -y --no-install-recommends -t bookworm-backports \
      weston \
      libgl1-mesa-dri \
      libegl-mesa0 \
      libglx-mesa0 \
      libgbm1 \
      mesa-vulkan-drivers \
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
