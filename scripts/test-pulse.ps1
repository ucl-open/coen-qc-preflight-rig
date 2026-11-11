# Checks BoardWrites' Pulse (src\Extensions\Preflight.cs) without the rig: a DO line's pulse timed by
# the board (its PulseDO width, OutputPulseEnable, OutputSet, never a clear from the PC), so a Flush
# stopped mid-way still ends. Build the extension first:
#   dotnet build src\Extensions.csproj -p:LangVersion=5 -o %TEMP%\pf-ext-build
# Exit 0 when every check passes.
$ErrorActionPreference = 'Stop'
$bin = Join-Path ([IO.Path]::GetTempPath()) 'pf-ext-build'
Get-ChildItem $bin -Filter *.dll | ForEach-Object { try { $null = [Reflection.Assembly]::LoadFrom($_.FullName) } catch {} }
$failed = 0
function Check($name, $ok) {
    if ($ok) { "  ok    $name" } else { "  FAIL  $name"; $script:failed++ }
}
$rx = [System.Reactive.Linq.Observable]
$types = [AppDomain]::CurrentDomain.GetAssemblies() | ForEach-Object { try { $_.GetTypes() } catch { $_.Exception.Types } }
$writes = $types | Where-Object { $_ -and $_.Name -eq 'BoardWrites' } | Select-Object -First 1
$message = $types | Where-Object { $_ -and $_.FullName -eq 'Bonsai.Harp.HarpMessage' } | Select-Object -First 1
function Of($type, $values) { ($rx.GetMethods() | Where-Object { $_.Name -eq 'ToObservable' -and $_.GetParameters().Count -eq 1 })[0].MakeGenericMethod($type).Invoke($null, @(, [string[]]$values)) }

# Each write the board gets for these commands, as "address=value", with the time the run took.
function Run([string[]]$commands) {
    $w = [Activator]::CreateInstance($writes)
    $w.Device = 'BehaviorBoard-Main'; $w.Kind = 'behaviorBoard'; $w.PulseWidths = 'DO1=40,DO2=45'
    $source = $w.Process((Of ([string]) $commands), (Of ([string]) @()), (Of ([string]) @()))
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $out = @(($rx.GetMethods() | Where-Object { $_.Name -eq 'ToEnumerable' })[0].MakeGenericMethod($message).Invoke($null, @($source)))
    [pscustomobject]@{ Writes = (@($out | ForEach-Object { "$($_.Address)=$($_.GetPayloadUInt16())" }) -join ' '); Ms = $clock.ElapsedMilliseconds }
}

$r = Run @()
Check "opening clears OutputPulseEnable ($($r.Writes))" ($r.Writes -eq '45=0')
$r = Run @('BehaviorBoard-Main DO1 10000')
Check "Flush: the board times 10 s on DO1, no clear from here ($($r.Writes))" ($r.Writes -eq '45=0 57=10000 45=2048 34=2048')
Check "Flush: returns at once, not after 10 s ($($r.Ms) ms)" ($r.Ms -lt 2000)
$r = Run @('BehaviorBoard-Main DO1', 'BehaviorBoard-Main DO2', 'BehaviorBoard-Main DO3')
Check "Pulse: rig.yml's width, else 50 ms; each line kept enabled ($($r.Writes))" ($r.Writes -eq '45=0 57=40 45=2048 34=2048 58=45 45=6144 34=4096 59=50 45=14336 34=8192')
$r = Run @('BehaviorBoard-Main DOPort0 30')
Check "DOPort: set, then cleared from here ($($r.Writes))" ($r.Writes -eq '45=0 34=1 35=1')
foreach ($bad in 'BehaviorBoard-Main DO1 99999', 'BehaviorBoard-Main DO1 0', 'BehaviorBoard-Main DO9', 'BehaviorBoard-Main', 'BehaviorBoard-Main DO1 10 x', 'Other-Board DO1 10000') {
    $r = Run @($bad)
    Check "'$bad' fires nothing" ($r.Writes -eq '45=0')
}

if ($failed) { "test-pulse: $failed failed."; exit 1 }
"test-pulse: all passed."
