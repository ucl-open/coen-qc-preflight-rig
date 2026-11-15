# The lab's Bonsai setup is one copy in lib-open-rigs (open_rigs\scripts\setup-bonsai.ps1): it runs here from the
# open-rigs this repository pins, so every workflow sets Bonsai up alike and a fix reaches all of them with the pin.
# uv sync --locked installs uv.lock as committed and fails rather than rewrite it, so a rig never changes the pins.
$repo = Split-Path -Parent $PSScriptRoot
if (-not (Get-Command uv -ErrorAction SilentlyContinue)) {
    [Console]::Error.WriteLine("uv is not on PATH, so Bonsai was not set up in ${repo}: install uv and run this again.")
    exit 1
}
& uv sync --locked --project $repo -q
if ($LASTEXITCODE -ne 0) {
    [Console]::Error.WriteLine("uv sync --locked failed in ${repo} (exit $LASTEXITCODE), so Bonsai was not set up: run uv sync --locked there to see why.")
    exit 1
}
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repo '.venv\Lib\site-packages\open_rigs\scripts\setup-bonsai.ps1') -Environment $PSScriptRoot
exit $LASTEXITCODE
