# Checks BoardCommands' Pulse and Reset (src\Extensions\Preflight.cs) without the rig: the writes it puts into the
# board piece's Commands subject. A DO line's pulse is timed by the board (its PulseDO width, the line added to
# rig.yml's pulsed lines, OutputSet, never a clear from the PC), so a Flush stopped mid-way still ends; a CameraTrigger
# line is never pulsed. Build the extension first:
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
function TypeOf($name) { $types | Where-Object { $_ -and $_.FullName -eq $name } | Select-Object -First 1 }
$commands = TypeOf 'BoardCommands'
$message = TypeOf 'Bonsai.Harp.HarpMessage'
$named = (TypeOf 'OpenRigs.Core.NamedDevice`1').MakeGenericType((TypeOf 'OpenRigs.Devices.Rigs.BehaviorBoard'))
function Of($type, $values) { ($rx.GetMethods() | Where-Object { $_.Name -eq 'ToObservable' -and $_.GetParameters().Count -eq 1 })[0].MakeGenericMethod($type).Invoke($null, @(, $values)) }

# rig.yml's board: DO1 to DO3 fixed widths 40, 45, 3000 ms, pulse-enabled as $enabled; DO0 wired to the camera trigger.
function Board([string]$enabled = 'DO1, DO2, DO3') {
    $board = [Activator]::CreateInstance((TypeOf 'OpenRigs.Devices.Rigs.BehaviorBoard'))
    $pulses = [Activator]::CreateInstance((TypeOf 'OpenRigs.Devices.Rigs.PulseController'))
    $pulses.OutputPulseEnable = [Enum]::Parse((TypeOf 'Harp.Behavior.DigitalOutputs'), $enabled)
    $outputs = [Activator]::CreateInstance((TypeOf 'OpenRigs.Devices.Rigs.PulseOutputs'))
    foreach ($line in @(('DO1', 40), ('DO2', 45), ('DO3', 3000))) {
        $fixed = [Activator]::CreateInstance((TypeOf 'OpenRigs.Devices.Rigs.FixedPulse')); $fixed.PulseWidthMs = $line[1]
        $outputs.($line[0]) = $fixed
    }
    $pulses.Outputs = $outputs; $board.PulseController = $pulses
    $wiring = [Activator]::CreateInstance((TypeOf 'OpenRigs.Devices.Rigs.BoardWiring'))
    $camera = [Activator]::CreateInstance((TypeOf 'OpenRigs.Devices.Rigs.WiredLine')); $camera.Device = 'CameraTrigger'
    $wiring.DO0 = $camera; $board.Wiring = $wiring
    [Activator]::CreateInstance($named, @('BehaviorBoard-Main', $board))
}

# Each write the board gets for these commands, as "address=value", with the time the run took.
function Run([string[]]$pulses, [string[]]$resets = @(), [string]$enabled = 'DO1, DO2, DO3') {
    $entries = [Array]::CreateInstance($named, 1); $entries[0] = Board $enabled
    $source = ([Activator]::CreateInstance($commands)).Process((Of $named $entries), (Of ([string]) ([string[]]$pulses)), (Of ([string]) ([string[]]$resets)))
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $out = @(($rx.GetMethods() | Where-Object { $_.Name -eq 'ToEnumerable' })[0].MakeGenericMethod($message).Invoke($null, @($source)))
    [pscustomobject]@{ Writes = (@($out | ForEach-Object { "$($_.Address)=$($_.GetPayloadUInt16())" }) -join ' '); Ms = $clock.ElapsedMilliseconds }
}

$r = Run @()
Check "nothing is written until a command: the pieces set the board up ($($r.Writes))" ($r.Writes -eq '')
$r = Run @('BehaviorBoard-Main DO1 10000')
Check "Flush: the board times 10 s on DO1, rig.yml's pulsed lines kept, no clear from here ($($r.Writes))" ($r.Writes -eq '57=10000 45=14336 34=2048')
Check "Flush: returns at once, not after 10 s ($($r.Ms) ms)" ($r.Ms -lt 2000)
$r = Run @('BehaviorBoard-Main DO1', 'BehaviorBoard-Main DO2', 'BehaviorBoard-Main DO3')
Check "Pulse: rig.yml's width on each line ($($r.Writes))" ($r.Writes -eq '57=40 45=14336 34=2048 58=45 45=14336 34=4096 59=3000 45=14336 34=8192')
$r = Run @('BehaviorBoard-Main DO2') @() 'DO1'
Check "Pulse on a line rig.yml does not pulse: added to its pulsed lines, never in their place ($($r.Writes))" ($r.Writes -eq '58=45 45=6144 34=4096')
$r = Run @('BehaviorBoard-Main DO0')
Check "the CameraTrigger line is never pulsed ($($r.Writes))" ($r.Writes -eq '')

# Reset (Pip, 11 Nov 2026: "End test should reset values and close"): every DO line but the camera trigger closed,
# rig.yml's widths and pulsed lines back.
$r = Run @() @('')
Check "Reset: DO1-3 closed, DO0 left to the camera trigger, widths and pulsed lines as rig.yml ($($r.Writes))" ($r.Writes -eq '35=14336 57=40 58=45 59=3000 45=14336')
$r = Run @() @('Other-Board')
Check "Reset for another board writes nothing here ($($r.Writes))" ($r.Writes -eq '')
$r = Run @('BehaviorBoard-Main DOPort0 30')
Check "DOPort: set, then cleared from here ($($r.Writes))" ($r.Writes -eq '34=1 35=1')
foreach ($bad in 'BehaviorBoard-Main DO1 99999', 'BehaviorBoard-Main DO1 0', 'BehaviorBoard-Main DO9', 'BehaviorBoard-Main', 'BehaviorBoard-Main DO1 10 x', 'Other-Board DO1 10000') {
    $r = Run @($bad)
    Check "'$bad' fires nothing" ($r.Writes -eq '')
}

if ($failed) { "test-pulse: $failed failed."; exit 1 }
"test-pulse: all passed."
