#!/usr/bin/env bash
# Self-contained publish of the LazerRender web service for deployment on a server
# that does not have the ASP.NET Core runtime installed.
#
# The output folder bundles the .NET runtime, so it runs with only glibc/OpenSSL present.
#
# Usage:
#   LazerRender.Service/scripts/publish-service.sh [runtime] [output-dir]
#
# Defaults: linux-x64, ./LazerRender.Service/publish

set -euo pipefail

SERVICE_DIR="$(cd "$(dirname "$0")/.." && pwd)"
RID="${1:-linux-x64}"
OUT="${2:-$SERVICE_DIR/publish}"
PROJECT="$SERVICE_DIR/src/LazerRender.Api/LazerRender.Api.csproj"

echo "Publishing LazerRender web service"
echo "  runtime: $RID"
echo "  output:  $OUT"

# -p:DebugType=None drops PDBs; appsettings.Development.json is excluded in the csproj.
# Secrets/OAuth credentials are NEVER written to appsettings.json — supply them via environment
# variables or a systemd EnvironmentFile (see LazerRender.Service/DEPLOYMENT.md).
dotnet publish "$PROJECT" \
    -c Release \
    -r "$RID" \
    --self-contained true \
    -p:DebugType=None \
    -p:DebugSymbols=false \
    -o "$OUT"

echo "Done. The deployment directory is: $OUT"
echo "Launch from that directory so runtime data is created next to the app:"
echo "  cd $OUT && ./LazerRender.Api"
