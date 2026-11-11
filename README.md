# coen-qc-preflight-rig

The Coen Lab's pre-training preflight: one Bonsai workflow for every rig, checking that each device the rig's `rig.yml` declares works. rig-control refuses a launch (the `test` mouse aside) without a pass in the last 18 h, once its agent runs with `--preflight`.

**Prototype (7 Nov 2026).** Run headless on PIPOFFICERIG only; not yet on a rig, and not in rig-setup's Repos.

## How it works

Run from rig-control's Preflight card through `OpenRigs.Control`'s `ExperimentControl` (port 5561, no parameters). `src\main.bonsai`, in groups:

- **Devices**: `PreflightDevices` (`src\Extensions\Preflight.cs`) reads `<ConfigRoot>\<MACHINE>\rig.yml` as lib-open-rigs' `RigDevices` does. One `SelectMany` per family opens each entry and keeps it open until Stop:
  - Harp boards (`behaviorBoard`, `timestampGeneratorGen3`, `stepperDriver`, `licketySplit`): `harp:Device` on `portName`, messages counted. A behaviour board with `cameraTriggerController.trigger0Enabled` starts camera trigger 0 (DO0) at `trigger0Frequency` when it opens (`BoardWrites`); the board stops it itself on going to Standby when the workflow stops.
  - Cameras: OpenRigs.Video's `TriggeredSpinnaker` by `serialNumber`, as the experiments open them (hardware triggered, rig.yml's `exposureTime`, `gain`, `binning` and `pixelFormat`, Mono8 when not set), frames counted. With no trigger running a camera sends nothing.
  - `projectionCalibration`: the projector's window with the rig's mesh map and gamma lookup and a test pattern, frames rendered counted; closed once its `Confirm` comes after Go (the render chain first, on its own thread: see the annotation).
  - `speakerArray`: nothing opened; only the person's answer.
  `DeviceMonitor` reports what each device sent every second, or its error once, so one dead device never ends the run.
- **Ready**: once every device has opened (sent something, or 5 s without failing) or failed, phase `Ready`, and from then the `devices` metric once a second: `;`-separated `<Device> <measure> <rate>` (per second over the last second, such as `Camera-Body frames 59.9`) or `<Device> error <detail>`, in rig.yml's order; a speaker array reads `SpeakerArray person 0`.
- Command `Pulse` (`BehaviorBoard-Main DO1`, any time): the line set, then cleared after its `pulseWidthMs` in rig.yml, else 50 ms. Behaviour boards only; lines DO0-DO3, DOPort0-DOPort2.
- **Check**, from Go (`Recording`): each open device counted in turn for 5 s from its reports, nothing reopened (`step` metric `<n>/<total> <Device>`); then each device a person confirms asked in turn (`prompt` metric `<Device>: <question>`, empty otherwise) until `Confirm` (`<Device> yes` or `<Device> no`; the first after Go is kept). Rows go to the `RawFile` launch property (`Device,Measure,Value,Detail`) and to stderr; the file is closed, then `Finished` 1.5 s later.
- `coen_qc_preflight_rig.judge` (Bonsai measures, Python computes) applies `config\thresholds.yml` and writes the pass record rig-control reads: `<RigConfigs>\<RIG>\preflight\last.json`. Every declared device must report, and nothing else may.

```
scripts\deploy.ps1          # Bonsai and its packages into .bonsai\, as rig-setup does
uv run coen-qc-preflight-rig plan <rig.yml>
uv run coen-qc-preflight-rig judge <rig.yml> <raw.csv> <out.json> --rig <RIG>   # dated by the raw file; the check's version is the last commit to src or config, as rig-control reads it
.bonsai\Bonsai.exe src\main.bonsai --no-editor --property:ConfigRoot=<dir> --property:RawFile=<csv> --property:SubjectId=test --property:SessionId=001 [--property:ProjectionWindowState=Normal --property:ProjectionWidth=640 --property:ProjectionHeight=360]
uv run --project <rig-control> rig-control status|go|command|stop localhost --port 5561   # drive it with no agent
```

## Not yet

- A test sound for the speaker question: the workflow asks it but plays nothing.
- Other checks a person confirms (a drop seen at each reward valve, the wheel turned and counted).
- The behaviour board starting its camera trigger, without which a triggered camera counts no frames.
- Live view: OpenRigs.LiveView's `StreamDevices` streams only `<Device>.Data` subjects it finds by name when the workflow is built, and these devices are opened per rig.yml entry inside `SelectMany`s, whose subjects it cannot see. It needs a mode that takes devices at run time (such as one subject of named items, or a per-device node inside the branch).
- A Stop before the projector's `Confirm` tears its window down from the control server's thread.
- Thresholds set on a rig.
- rig-setup's Repos, which also puts it under lib-open-rigs' update-workflows; until then its `local_packages` are the lab's as of 7 Nov 2026.
- `PreflightDevices` and `CheckDevice` moved into lib-open-rigs with tests, if the design holds on a rig.
