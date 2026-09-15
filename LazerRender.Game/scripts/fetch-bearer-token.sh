#!/usr/bin/env bash

set -euo pipefail

# OAuth client-credentials grant for the osu! API v2 (public scope).
#
# Credentials are supplied through the environment and must never be committed:
#   OSU_OAUTH_CLIENT_ID
#   OSU_OAUTH_CLIENT_SECRET
#
# Get them from https://osu.ppy.sh/home/account/edit#oauth

CLIENT_ID="${OSU_OAUTH_CLIENT_ID:-}"
CLIENT_SECRET="${OSU_OAUTH_CLIENT_SECRET:-}"

if [[ -z "${CLIENT_ID}" || -z "${CLIENT_SECRET}" ]]; then
    echo "OSU_OAUTH_CLIENT_ID and OSU_OAUTH_CLIENT_SECRET must be set." >&2
    exit 1
fi

# Perform POST request and parse access_token using jq
ACCESS_TOKEN=$(curl --silent --show-error --fail \
    --request POST "https://osu.ppy.sh/oauth/token" \
    --header "Accept: application/json" \
    --header "Content-Type: application/x-www-form-urlencoded" \
    --data-urlencode "client_id=${CLIENT_ID}" \
    --data-urlencode "client_secret=${CLIENT_SECRET}" \
    --data-urlencode "grant_type=client_credentials" \
    --data-urlencode "scope=public" \
    | jq -r '.access_token')

echo "${ACCESS_TOKEN}"
