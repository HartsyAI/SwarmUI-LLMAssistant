#!/usr/bin/env bash
# Runs the browser e2e suite. See E2E/README.md.
set -euo pipefail
cd "$(dirname "$0")"
if [ ! -d node_modules ]; then
    npm install --no-audit --no-fund
    npx playwright install --with-deps chromium
fi
exec npx playwright test "$@"
