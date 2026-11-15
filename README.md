# coen-qc-preflight-rig

The Coen Lab's pre-training preflight: one Bonsai workflow for every rig, checking that each device the rig's `rig.yml` declares works. rig-control refuses a launch (the `test` mouse aside) without a pass in the last 18 h, once its agent runs with `--preflight`.

**Prototype (7 Nov 2026).** Run headless on PIPOFFICERIG only; not yet on a rig. It is in `K:\config\profile-repos.json`, so app-rig-setup's `workflows\update-workflows.ps1` bumps its packages.

## How it works

Run from rig-control's Preflight card through `OpenRigs.Control`'s `ExperimentControl` (port 5561, no parameters). `src\main.bonsai`, in groups:

- **Devices**: `PreflightDevices` (`src\Extensions\Preflight.cs`) reads `rig.yml` in the folder OpenRigs.Core's `ResolveRigConfigDirectory` gives (`<ConfigRoot>\<MACHINE>`), as lib-open-rigs' `RigDevices` does. One `SelectMany` per family opens each entry and keeps it open until Stop:
  - Harp boards (`behaviorBoard`, `timestampGeneratorGen3`, `stepperDriver`, `licketySplit`): `harp:Device` on `portName`, messages counted. A behaviour board with `cameraTriggerController.trigger0Enabled` starts camera trigger 0 (DO0) at `trigger0Frequency` when it opens (`BoardWrites`); the board stops it itself on going to Standby when the workflow stops.
  - Cameras: OpenRigs.Video's `TriggeredSpinnaker` by `serialNumber`, as the experiments open them (hardware triggered, rig.yml's `exposureTime`, `gain`, `binning` and `pixelFormat`, Mono8 when not set), frames counted. With no trigger running a camera sends nothing.
  - `projectionCalibration`: the projector's window with the rig's mesh map and gamma lookup (OpenRigs.Vision `ProjectorDisplay`, the dome tasks' window) and a test pattern drawn on its `DrawStimuli`, frames rendered counted, then the person asked "Does the test pattern fill the screen?"; closed once its `Confirm` comes after Go, its GL resources freed on its render thread (see the annotation).
  - `speakerArray` (group `SpeakerTest`, only on a rig whose rig.yml has one, so a behaviour box is neither played to nor asked): lib-open-rigs' `SpeakerMixer` (`Speakers`, every filter checked, the mixer started no sooner than 3 s after Start) and a test sound for each speaker, a 0.3 s 1 kHz tone then 0.3 s of silence (Bonsai.Dsp `FunctionGenerator`), laid out by OpenRigs.Audio's `SpeakerBuffer` on that speaker's channel alone, equalised, at the lab's default level (-30 dBFS RMS). Nothing is counted: when the check asks "Did each speaker play the test sound?", every speaker plays its sound in turn, again every 5 s until its `Confirm`.
  `DeviceMonitor` reports what each device sent every second, or its error once, so one dead device never ends the run.
- **Ready**: once every device has opened (sent something, or 5 s without failing) or failed, phase `Ready`, and from then the `devices` metric once a second: `;`-separated `<Device> <measure> <rate>` (per second over the last second, such as `Camera-Body frames 59.9`) or `<Device> error <detail>`, in rig.yml's order; a speaker array reads `SpeakerArray person 0`.
- Command `Pulse` (`BehaviorBoard-Main DO1`, any time): one pulse of the line's `pulseWidthMs` in rig.yml, else 50 ms, or of a width in ms given third, 1 to 60000 (`BehaviorBoard-Main DO1 10000`, rig-control's Flush). Behaviour boards only; lines DO0-DO3, timed by the board (its `PulseDO` width and `OutputPulseEnable`, cleared as the workflow opens, so a pulse ends even if the workflow stops), and DOPort0-DOPort2, set and cleared from the PC. `scripts\test-pulse.ps1` checks the writes.
- Command `Reset` (empty for every board, or a board's name): each DO line closed at once (DO0 left to the camera trigger when it runs), each line's `PulseDO` width and `OutputPulseEnable` put back as rig.yml's `pulseController` has them. rig-control's End Testing, and closing its Preflight page, send it before the stop.
- Command `Move` (`StepperDriver-A Motor1 -150`): steps a stepper driver motor that rig.yml's wiring names, from where it is.
- Command `Home` (`StepperDriver-A Motor1`): zeroes a stepper driver motor as the lab's dome workflows do, off its home switch if on it, then towards zero until the switch stops it, then its position set to 0.
- **Check**, from Go (`Recording`): each open device counted in turn for 5 s from its reports, nothing reopened (`step` metric `<n>/<total> <Device>`); then each device a person confirms asked in turn (`prompt` metric `<Device>: <question>`, empty otherwise) until `Confirm` (`<Device> yes` or `<Device> no`; the first after Go is kept). Rows go to the `RawFile` launch property (`device,measure,value,detail`, OpenRigs.Logging's `CsvWriter`) and to stderr; the file is closed, then `Finished` 1.5 s later.
- `coen_qc_preflight_rig.judge` (Bonsai measures, Python computes) applies `config\thresholds.yml` and writes the pass record rig-control reads: `<RigConfigs>\<RIG>\preflight\last.json`. Every declared device must report, and nothing else may.

```
scripts\deploy.ps1          # Bonsai and its packages into .bonsai\, as rig-setup does
uv run coen-qc-preflight-rig plan <rig.yml>
uv run coen-qc-preflight-rig judge <rig.yml> <raw.csv> <out.json> --rig <RIG>   # dated by the raw file; the check's version is the last commit to src or config, as rig-control reads it
.bonsai\Bonsai.exe src\main.bonsai --no-editor --property:ConfigRoot=<dir> --property:RawFile=<csv> --property:SubjectId=test --property:SessionId=001 [--property:ProjectionWindowState=Normal --property:ProjectionWidth=640 --property:ProjectionHeight=360]
uv run --project <rig-control> rig-control status|go|command|stop localhost --port 5561   # drive it with no agent
```

## Not yet

- Other checks a person confirms (a drop seen at each reward valve, the wheel turned and counted).
- Thresholds set on a rig.
- `PreflightDevices` and `CheckDevice` moved into lib-open-rigs with tests, if the design holds on a rig.
