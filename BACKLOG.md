# Backlog

## 2026-11-14 Dome-task alignment audit

- **Log through the lab's loggers** (dome-task alignment audit, 14 Nov 2026, backlogged): `preflight-raw.csv` is written by Bonsai's own `CsvWriter`, not by the lab's loggers (lib-open-rigs OpenRigs.Logging) that the dome tasks use. Move it onto the lab's loggers when the coen- workflows' loggers are next aligned (the FFmpeg camera logger comes first).
