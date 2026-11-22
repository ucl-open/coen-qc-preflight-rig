# Backlog

History is in `git log`.

## Open

- Run both preflights on a rig: neither has run on one since the rig lists (16 Nov 2026), nor headless (a dome run opens the projector full screen).
- A dome stop can be held in BonVision's teardown (`ReplaySubject<OpenTK.Matrix4>` Unsubscribe under the draw chain, GREEN-DOME-2, 15 Nov 2026); `EndAfterStop` ends Bonsai 2 s after the stop meanwhile. Wants full stacks of a held stop (`GraceSeconds` 0 in a scratch copy).
- Other checks a person confirms: a drop seen at each reward valve, the wheel turned and counted.
- Thresholds set on a rig.

## Decided

- One workflow per rig kind on its rig list, no device opened by hand (Pip, 16 Nov 2026): `src\dome.bonsai` (`DomeHardware`, `config\task-dome.yml`) and `src\bbox.bonsai` (`BBoxHardware`, `config\task-bbox.yml`).
- Pulse and Reset are lib's `ManualPulses`; the spouts move by the lick spout stage's own commands.
- Speakers: evidence plus person (Pip, 15 Nov 2026): each test sound sent to the mixer is counted, then the person confirms.
- The raw rows go through OpenRigs.Logging's `CsvWriter`.
- `.bonsai\Setup.ps1` is the same in every task repo: each repo needs its own bootstrap.

## Not recommended

- No task-file schemas in preflight: the repo tests (`tests\test_preflight.py`, `tests\test_workflow.py`) already hold the keys.

## Audit round 5 (17 Nov 2026): not recommended

- task-dome.yml and task-bbox.yml as Preflight configs: they use the task-file mechanism.
- SpeakerArray as a Preflight row: it matches its rig.yml key.

## Audit 2026-11-22 (/audit-rigs, pipeline)

Changed (9826ffa): `check_commit` leaves out `src/Extensions.csproj`, so a package bump keeps a pass standing, as app-rig-control's `preflight.check_commit` (P3Q-M1); CI runs `scripts\check-workflow.ps1` in place of the C# 5 and parse steps (P6S-L1).

Deferred: a manual Checks run of the new step.

Not changed, do not raise again without new information:

- None in this repo this round.
