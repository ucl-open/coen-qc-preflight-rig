# Backlog

## Audit round 3 of 3 (15 Nov 2026)

- Play a test sound through lib's SpeakerMixer at its default level and ask again. The question "Did each speaker play the test sound?" was removed on 15 Nov 2026 because nothing played one; rig.yml's `speakerArray` is not checked until then. The projector's question says "fill the screen", not the dome.
- Done 15 Nov 2026: `scripts\deploy.ps1` exits 1 naming `.bonsai\Setup.ps1` when it is missing (a missing script left the last exit code, 0).

## Audit round 3 of 3 (15 Nov 2026): items not changed

- `.bonsai\Setup.ps1` is the same 15 lines in all five task repos: each repo needs its own bootstrap.

## 2026-11-14 Dome-task alignment audit

- **Done 15 Nov 2026: log through the lab's loggers.** `preflight-raw.csv` is written by OpenRigs.Logging's `CsvWriter` (columns `device`, `measure`, `value`, `detail`). Was: written by Bonsai's own `CsvWriter` (dome-task alignment audit, 14 Nov 2026).
