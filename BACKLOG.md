# Backlog

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
