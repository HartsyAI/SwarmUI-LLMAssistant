# Runs the browser e2e suite. See E2E/README.md.
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
if (-not (Test-Path node_modules)) {
    npm install --no-audit --no-fund
    npx playwright install chromium
}
npx playwright test @args
exit $LASTEXITCODE
