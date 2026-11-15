# The lab's Bonsai setup is one copy in lib-open-rigs (open_rigs\scripts\setup-bonsai.ps1): it runs here from the
# open-rigs this repository pins, so every workflow sets Bonsai up alike and a fix reaches all of them with the pin.
$repo = Split-Path -Parent $PSScriptRoot
& uv sync --project $repo -q
if ($LASTEXITCODE -ne 0) { "uv sync failed in $repo, so Bonsai was not set up: run uv sync there to see why."; exit $LASTEXITCODE }
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repo '.venv\Lib\site-packages\open_rigs\scripts\setup-bonsai.ps1') -Environment $PSScriptRoot
exit $LASTEXITCODE
