# The lab's workflow check for each of its workflows (src\dome.bonsai, src\box.bonsai): lib-open-rigs' open_rigs\scripts\check-workflow.ps1, from the
# open-rigs this repo pins (uv sync puts it in .venv), once per workflow. Needs .bonsai\Setup.ps1 run first. Exit 0 when all pass.
$repo = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
& uv sync --project $repo -q
if ($LASTEXITCODE -ne 0) { throw 'uv sync failed.' }
$check = Join-Path $repo '.venv\Lib\site-packages\open_rigs\scripts\check-workflow.ps1'
$failed = 0
foreach ($workflow in 'dome.bonsai','box.bonsai') {
    & powershell -NoProfile -ExecutionPolicy Bypass -File $check -Repo $repo -Workflow $workflow
    if ($LASTEXITCODE -ne 0) { $failed++ }
}
exit $(if ($failed) { 1 } else { 0 })
