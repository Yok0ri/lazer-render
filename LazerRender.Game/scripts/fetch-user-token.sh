#!/usr/bin/env bash

set -euo pipefail

# Exchanges an osu! OAuth v2 authorization code for a *user* access token + refresh token.
#
# LazerRender needs a user token (not a client-credentials token) to sign the render engine into
# lazer's API provider, which is what makes online beatmap leaderboards / the `scoreboard` HUD
# element work. Client-credentials tokens are guest-scoped and cannot satisfy lazer's `/me` check.
#
# Getting the code (one-off, in a browser while signed in as the bot account):
#   1. Register an OAuth application (https://osu.ppy.sh/home/account/edit#oauth) and note its
#      client id/secret and callback URL.
#   2. Visit:
#        https://osu.ppy.sh/oauth/authorize?client_id=<id>&redirect_uri=<callback>&response_type=code&scope=identify+public&state=lazerrender
#   3. Copy the `code` query parameter from the redirect you land on.
#
# Then:
#   OSU_OAUTH_CLIENT_ID=<id> OSU_OAUTH_CLIENT_SECRET=<secret> \
#   OSU_OAUTH_REDIRECT_URI=<callback> OSU_OAUTH_CODE=<code> scripts/fetch-user-token.sh
#
# Put the printed refresh token in `Renderer:OsuBotRefreshToken` (or the
# `Renderer__OsuBotRefreshToken` environment variable) so the service can keep it fresh.

CLIENT_ID="${OSU_OAUTH_CLIENT_ID:-}"
CLIENT_SECRET="${OSU_OAUTH_CLIENT_SECRET:-}"
REDIRECT_URI="${OSU_OAUTH_REDIRECT_URI:-}"
CODE="${OSU_OAUTH_CODE:-}"

if [[ -z "${CLIENT_ID}" || -z "${CLIENT_SECRET}" || -z "${CODE}" ]]; then
    echo "OSU_OAUTH_CLIENT_ID, OSU_OAUTH_CLIENT_SECRET and OSU_OAUTH_CODE must be set." >&2
    exit 1
fi

RESPONSE=$(curl --silent --show-error --fail \
    --request POST "https://osu.ppy.sh/oauth/token" \
    --header "Accept: application/json" \
    --header "Content-Type: application/x-www-form-urlencoded" \
    --data-urlencode "client_id=${CLIENT_ID}" \
    --data-urlencode "client_secret=${CLIENT_SECRET}" \
    --data-urlencode "code=${CODE}" \
    --data-urlencode "grant_type=authorization_code" \
    --data-urlencode "redirect_uri=${REDIRECT_URI}")

echo "Refresh token (treat it as a secret; it is not written to disk):"
echo "${RESPONSE}" | jq -r '.refresh_token'
