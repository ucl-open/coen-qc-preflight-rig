# coen-qc-preflight-rig

The Coen Lab's pre-training preflight: one Bonsai workflow for each rig kind, checking that each device the rig's `rig.yml` declares works. rig-control refuses a launch (the `test` mouse aside) without a pass in the last 18 h, once its agent runs with `--preflight`.

It is in `K:\config\profile-repos.json`, so app-rig-setup's `workflows\update-workflows.ps1` bumps its packages.

## How it works

One workflow per rig kind, each its rig list, the lab's check and its task file, with no device code of its own (Pip, 16 Nov 2026: "QC workflows use the lib pieces, never devices opened by hand"): `src\dome.bonsai` for the domes (`DomeHardware`, `config\task-dome.yml`) and `src\box.bonsai` for the behaviour boxes (`BehaviorBoxHardware`, `config\task-box.yml`), each rig list in a `Hardware` group as in the tasks. rig-control's Preflight page and card launch the one its rig.yml fits (its `calibrations.toml` `[[preflight.by_rig]]`: a rig.yml with a `projectionCalibration` is a dome's), as the test mouse, through `OpenRigs.Control`'s `ExperimentControl` (port 5561), and write the task file at Go as for a task. Each workflow, in groups:

- `ReadLabRig`, `NoExperimentFolder` (subject `_preflight_`, so nothing is recorded and the spouts go to rig.yml's `lickSpoutDefaultPosition`), `Initialization` (`ExperimentControl`, no commands of its own; `EndAfterStop`), the rig list, which opens and readies every device as a task does and reports `Ready` (its `ReadyGate`), then lib-open-rigs' `PreflightChecks` (`RawFile`, `TaskPath`).
- **Taps**: each piece's `<Name>.Data`, and on the domes the projector's frames (`DrawStimuli`, as `ProjectionCalibration`), into the check's `DeviceTicks` (`PreflightTap`). The rows are rig.yml's devices, so a device rig.yml has and the rig list lacks has a row, its error. The `devices` metric gives each device's rate once a second (`<Device> <measure> <rate>` or `<Device> error <detail>`; a speaker array `SpeakerArray sounds <n>`).
- **Pulse and Reset** are lib's `BehaviorBoard.ManualPulses` piece, in `Hardware`: `Pulse` (`BehaviorBoard-Main DO1`, rig-control's Flush with a width third, 1 to 60000 ms; else the line's `pulseWidthMs`, else 50 ms) timed by the board on every line, DO0 to DO3 and DOPort0 to DOPort2, so a pulse ends with its line low however the workflow stops, never on a `CameraTrigger` or `ZapitTrigger` line; `Reset` (rig-control's End Testing, before its stop) closes every DO and DOPort line but the camera trigger and puts the fixed widths and pulsed lines back as rig.yml has them. On the domes the spouts move by the lick spout stage's own commands (`SpoutsHome`, the nudges), never by register writes here.
- **SpeakerTest** (domes): each rig.yml speaker's test sound (a 0.3 s 1 kHz tone then 0.3 s of silence, OpenRigs.Audio `SpeakerBuffer` on its channel alone, equalised, at -30 dBFS RMS) through the rig list's mixer, in turn and every 5 s from the question "Did each speaker play the test sound?" until its `Confirm`; each sound the mixer is sent is counted per speaker (`CountSpeakerSounds` into `SpeakerSounds`): evidence plus person (Pip, 15 Nov 2026). **TestPattern** (domes): a white rectangle over the whole of `ProjectorDisplay`'s view (270 by 105 degrees, centred at 0, 30) until the stop.
- **The check**, from Go (`Recording`), in `PreflightChecks`: each device counted in turn for the task file's `countSeconds` (`step` metric), then each question of the task file asked in turn (`prompt` metric) until the controller's `Confirm` (`<Device> yes` or `<Device> no`); rows to `RawFile` (`device,measure,value,detail`), a speaker array's `SpeakerArray,sounds:<speaker>,<n>` before its `person` row; `Finished` once the file is closed.
- A device that fails as it opens (a port another program holds, a WhoAmI or firmware mismatch, a camera that never sends a frame, a clock never set) stops the preflight at Start naming it, as it stops a task: fix it and run again.
- `coen_qc_preflight_rig.judge` (Bonsai measures, Python computes) applies `config\thresholds.yml` and writes the pass record rig-control reads: `<RigConfigs>\<RIG>\preflight\last.json`. Every declared device must report, and nothing else may.

```
scripts\deploy.ps1          # Bonsai and its packages into .bonsai\, as rig-setup does
uv run coen-qc-preflight-rig plan <rig.yml>
uv run coen-qc-preflight-rig judge <rig.yml> <raw.csv> <out.json> --rig <RIG>   # dated by the raw file; the check's version is the last commit to src or config, as rig-control reads it
.bonsai\Bonsai.exe src\box.bonsai --no-editor --property:ConfigRoot=<dir> --property:RawFile=<csv> --property:TaskPath=config\task-box.yml --property:SubjectId=test --property:SessionId=001
uv run --project <rig-control> rig-control status|go|command|stop localhost --port 5561   # drive it with no agent
```

## Not yet

- Other checks a person confirms (a drop seen at each reward valve, the wheel turned and counted).
- Thresholds set on a rig.
