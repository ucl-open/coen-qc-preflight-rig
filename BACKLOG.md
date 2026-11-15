# Backlog

## Audit round 3 of 3 (15 Nov 2026)

- Done 15 Nov 2026 (Pip): each rig.yml speaker plays a test sound through lib's SpeakerMixer at its default level (`SpeakerBuffer`, -30 dBFS RMS), in turn and every 5 s from the question until its answer, and "Did each speaker play the test sound?" is asked again (its handling, threshold, plan and tests as before ba58ee9). Only a rig with a `speakerArray`. The preflight has no RigHardware (it opens each device itself), so it sets no `ReadyWaitsFor`: the sounds start at the question, long after the mixer's boot. A mixer that fails to start, or a speaker without a usable filter, stops the preflight with its error rather than failing one row. Not yet run on a rig. Was: the question removed on 15 Nov 2026 because nothing played one. The projector's question says "fill the screen", not the dome.
- Done 15 Nov 2026: `scripts\deploy.ps1` exits 1 naming `.bonsai\Setup.ps1` when it is missing (a missing script left the last exit code, 0).

## Audit round 3 of 3 (15 Nov 2026): items not changed

- `.bonsai\Setup.ps1` is the same 15 lines in all five task repos: each repo needs its own bootstrap.

## 2026-11-14 Dome-task alignment audit

- **Done 15 Nov 2026: log through the lab's loggers.** `preflight-raw.csv` is written by OpenRigs.Logging's `CsvWriter` (columns `device`, `measure`, `value`, `detail`). Was: written by Bonsai's own `CsvWriter` (dome-task alignment audit, 14 Nov 2026).
