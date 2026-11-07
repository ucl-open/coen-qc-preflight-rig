# coen-qc-preflight-rig

The Coen Lab's pre-training preflight: one Bonsai workflow for every rig, checking that each device the rig's `rig.yml` declares works. rig-control refuses a launch (the `test` mouse aside) without a pass in the last 18 h, once its agent runs with `--preflight`.

**Prototype (7 Nov 2026).** Run headless on PIPOFFICERIG only; not yet on a rig, and not in rig-setup's Repos.

## How it works

- `src\main.bonsai`: `PreflightDevices` (`src\Extensions\Preflight.cs`) reads `<ConfigRoot>\<MACHINE>\rig.yml` as lib-open-rigs' `RigDevices` does and lists the entries of the kinds asked for. One `SelectMany` per device family starts a branch for each entry, and nothing for a kind the rig does not declare:
  - Harp boards (`behaviorBoard`, `timestampGeneratorGen3`, `stepperDriver`, `licketySplit`): `harp:Device` on the entry's `portName`, messages counted.
  - Cameras: `SpinnakerCapture` by `serialNumber`, frames counted.
  - `projectionCalibration`: the projector's window with the rig's mesh map and gamma lookup, a test pattern, frames rendered counted.
- `CheckDevice` counts what a device sends in a window after a warm-up and turns a device that fails to open into an `error` row, so one dead device never ends the run. Rows go to the `RawFile` launch property (`Device,Measure,Value,Detail`) and to stderr.
- `coen_qc_preflight_rig.judge` (Bonsai measures, Python computes) applies `config\thresholds.yml` and writes the pass record rig-control reads: `<RigConfigs>\<RIG>\preflight\last.json`. Every declared device must report, and nothing else may.

```
scripts\deploy.ps1          # Bonsai and its packages into .bonsai\, as rig-setup does
uv run coen-qc-preflight-rig plan <rig.yml>
uv run coen-qc-preflight-rig judge <rig.yml> <raw.csv> <out.json> --rig <RIG> --commit <sha>
.bonsai\Bonsai.exe src\main.bonsai --no-editor --property:ConfigRoot=<dir> --property:RawFile=<csv> [--property:ProjectionWindowState=Normal --property:ProjectionWidth=640 --property:ProjectionHeight=360]
```

## Not yet

- The checks a person confirms at the end (speakers heard, projection seen), asked together after everything automatic.
- The behaviour board starting its camera trigger, without which a triggered camera counts no frames.
- `ExperimentControl`, so rig-control can launch it from a Preflight card on its Calibrations page.
- Thresholds set on a rig.
- rig-setup's Repos, which also puts it under lib-open-rigs' update-workflows; until then its `local_packages` are the lab's as of 7 Nov 2026.
- `PreflightDevices` and `CheckDevice` moved into lib-open-rigs with tests, if the design holds on a rig.
