# Backlog

## 2026-11-16 Review findings (fix6a)

- Done: a DOPort pulse was cleared by a PC timer and `Reset` cleared only DO0-DO3, so a stop or `EndAfterStop` during a DOPort pulse left that line high (a valve on it kept running). `BoardCommands` now has the board time every pulse, DOPort0-2 by `PulseDOPort0-2` (registers 46-48) as DO0-3 by `PulseDO0-3`, and `Reset` closes DOPort0-2 too. `scripts\test-pulse.ps1` models the board and stops 100 ms into a 500 ms pulse on DOPort0, DOPort2, DO1 and DO3: no line ends high (DOPort failed on the old code). Not yet run on a rig.

## 2026-11-16 Preflight per rig kind (lib-open-rigs 9845e12, Devices .30)

- Done (Pip, 16 Nov 2026, plan v2 item 4: "QC workflows use the lib pieces, never devices opened by hand"): one workflow per rig kind, `src\main.bonsai` on `DomeHardware` and `src\box.bonsai` on `BBoxHardware`, each with lib's `PreflightChecks` and a task file (`config\task-dome.yml`, `config\task-box.yml`) that rig-control chooses by the rig's rig.yml and writes at Go. Deleted: `PreflightDevices`, `DeviceMonitor`, `RetryPortOpen`, the device-opening branches, the top-level `HarpDeviceData`/`CameraDeviceData` live-view stand-ins, `ShareDeviceData`, `CloseWhen`, the own Ready group, and `BoardWrites` but for Pulse and Reset (`BoardCommands`) and Move and Home (`StepperCommands`), now written into the pieces' command subjects. The rows are rig.yml's devices, so a device the rig list lacks has a row (its error); a fault beyond presence stops the preflight naming the device, as it stops a task. Changed: Pulse adds its line to rig.yml's pulsed lines (it replaced them) and refuses a CameraTrigger or ZapitTrigger line; the projector's window stays open until the stop. Checked: pytest (`tests\test_workflow.py` holds both to workflow_lint), check-workflow-builds, `box.bonsai` built with Bonsai's builders, `scripts\test-pulse.ps1`. Not yet run on a rig, nor headless (a dome run opens the projector full screen).
- Done 16 Nov 2026: check-workflow-builds builds `src\main.bonsai` and every other top-level `src\*.bonsai` with ExperimentControl, so `box.bonsai` is built (app-rig-setup).

## 2026-11-16 Device pieces (lib-open-rigs 5ea5729)

- Done: lib 5ea5729's packages; `tests\fixtures\BBOX-1.yml` as the share's BBOX-1 rig.yml after the cut (camera `triggerFrequency` gone, which the lab rig model refuses; `pixelFormat: Mono8`; DO0 CameraTrigger wiring to Camera-Overhead, which the model requires for a camera). pytest, check-workflow-builds and `scripts\test-pulse.ps1` green.
- Not yet, waiting for Pip: the preflight onto the lab's pieces (Pip, 16 Nov 2026: QC workflows use the same pieces as the tasks). The pieces stop the whole workflow on a dead or missing device, where the preflight reports each device's failure as its own row; the decisions and a design are in the session's handoff (DomeHardware, the preflight's monitors reading the pieces' `.Data`, `BoardWrites` cut to Pulse, Reset, Move and Home into the pieces' command subjects, the own Ready, RigFile, ReadyStages and mixer deleted).

## Rig test, GREEN-DOME-2 (15 Nov 2026)

- Done: End Testing's stop at the speaker question left Bonsai running until the agent's kill 30 s later. Headless Bonsai returns from Main when its runner has the stop's error and tears the workflow down on the control server's thread; the process then lasts while a foreground thread does, here only the live view's publishers (StreamController's `ObserveOn` NewThreadScheduler), which end when the teardown reaches StreamDevices, last. Reproduced headless with a test ConfigRoot (fake COM ports, RDP): about 1 stop in 6 at the speaker question was held, the speakers still playing every 5 s after the stop; ClrMD stacks showed the teardown blocked in `ReplaySubject<OpenTK.Matrix4>` Unsubscribe under the projection's BonVision draw chain, which is disposed from the control thread while the render thread draws. `EndAfterStop` (Initialization) ends headless Bonsai 2 s after the stop, saying so on stderr. Not yet: the BonVision teardown itself (a dome task's stop was held the same way, 14 Nov 2026 17:35), which wants full stacks of a held stop (set `GraceSeconds` to 0 in a scratch copy).
- Done: evidence the speakers played (Pip: evidence plus person). Each sound the mixer is sent is counted per rig.yml speaker (`CountSpeakerSounds`): stderr, the devices metric `SpeakerArray sounds <n>` and the rows `SpeakerArray,sounds:<speaker>,<n>`; thresholds `minSounds: 1`, judged before the person's answer. It counts sounds handed to the mixer, not sound heard: the person still confirms.
- Done: `CloseWhen` takes an error on its signal (the stop through ProjectionClose) as a close; it threw it on the control server's thread.

## Audit round 3 of 3 (15 Nov 2026)

- Done 15 Nov 2026 (Pip): each rig.yml speaker plays a test sound through lib's SpeakerMixer at its default level (`SpeakerBuffer`, -30 dBFS RMS), in turn and every 5 s from the question until its answer, and "Did each speaker play the test sound?" is asked again (its handling, threshold, plan and tests as before ba58ee9). Only a rig with a `speakerArray`. The preflight has no RigHardware (it opens each device itself), so it sets no `ReadyWaitsFor`: the sounds start at the question, long after the mixer's boot. A mixer that fails to start, or a speaker without a usable filter, stops the preflight with its error rather than failing one row. Not yet run on a rig. Was: the question removed on 15 Nov 2026 because nothing played one. The projector's question says "fill the screen", not the dome.
- Done 15 Nov 2026: `scripts\deploy.ps1` exits 1 naming `.bonsai\Setup.ps1` when it is missing (a missing script left the last exit code, 0).

## Audit round 3 of 3 (15 Nov 2026): items not changed

- `.bonsai\Setup.ps1` is the same 15 lines in all five task repos: each repo needs its own bootstrap.

## 2026-11-14 Dome-task alignment audit

- **Done 15 Nov 2026: log through the lab's loggers.** `preflight-raw.csv` is written by OpenRigs.Logging's `CsvWriter` (columns `device`, `measure`, `value`, `detail`). Was: written by Bonsai's own `CsvWriter` (dome-task alignment audit, 14 Nov 2026).
