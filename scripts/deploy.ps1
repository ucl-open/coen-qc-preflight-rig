# Installs Bonsai and the workflow's packages into .bonsai\. This project has no rig schema of its
# own: the workflow reads the rig's existing <ConfigRoot>\<RIG>\rig.yml, whatever its kind,
# and its Python (coen_qc_preflight_rig.judge) writes the pass record into preflight\ beside it.
$repo = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$setup = Join-Path $repo '.bonsai\Setup.ps1'
# A missing script is a non-terminating error and leaves $LASTEXITCODE as it was, so check first.
if (-not (Test-Path -Path $setup)) { Write-Error ".bonsai\Setup.ps1 was not found in $repo."; exit 1 }
& $setup
exit $LASTEXITCODE
