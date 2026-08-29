#!/usr/bin/env bash
# Build command for the Cloudflare Workers Builds dashboard (Settings > Build).
# Deploy and the non-production deploy command are separate dashboard fields
# (defaults: `npx wrangler deploy` / `npx wrangler versions upload`) and read
# the Wrangler version from package.json, so this script only regenerates dist/.
set -euo pipefail
cd "$(dirname "$0")/.."

go run ./tools/vpmbuild listing -out dist
