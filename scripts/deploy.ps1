# Installs Bonsai and the workflow's packages into .bonsai\. This project has no rig schema of its
# own: the workflow reads the rig's existing <ConfigRoot>\<MACHINE-NAME>\rig.yml, whatever its kind,
# and its Python (coen_qc_preflight_rig.judge) writes the pass record into preflight\ beside it.
$repo = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
& (Join-Path $repo '.bonsai\Setup.ps1')
exit $LASTEXITCODE
