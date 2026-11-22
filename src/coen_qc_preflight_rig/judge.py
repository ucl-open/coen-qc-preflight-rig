"""Whether a rig passed: the workflow's raw measurements against config/thresholds.yml.

The workflow writes one row per measurement, `device,measure,value,detail`: a device that failed to open
writes `error` with the message; a Harp board `messages` and `seconds`; a camera `frames` and `seconds`;
a section a person confirms `person` (1 yes, 0 no); a speaker array also `sounds:<speaker>` for each rig.yml
speaker, the test sounds the mixer was sent on its channel. Every device rig.yml declares must report, and
nothing else may: a missing or extra device fails the check, so a workflow that starts the wrong
branches never passes.
"""

import csv
import hashlib
import json
import subprocess
from datetime import datetime, timezone
from pathlib import Path

import yaml

from coen_qc_preflight_rig.plan import Planned, plan
from coen_qc_preflight_rig.rig import load
from open_rigs.rigs.lab_rig import LabRig


def read_raw(path: Path) -> dict[str, dict[str, tuple[str, str]]]:
    """{device: {measure: (value, detail)}}; a later row for the same measure wins."""
    rows: dict[str, dict[str, tuple[str, str]]] = {}
    with open(path, newline="", encoding="utf-8-sig") as f:
        for row in csv.DictReader(f):
            rows.setdefault(row["device"], {})[row["measure"]] = (row.get("value") or "", row.get("detail") or "")
    return rows


def _number(measures: dict, key: str) -> float | None:
    try:
        return float(measures[key][0])
    except (KeyError, ValueError):
        return None


def _speakers(device: Planned) -> list[str]:
    """A speaker array's speakers, by their rig.yml names; none for any other device."""
    return list(getattr(device.settings, "speakers", None) or {})


def _trigger_hz(rig: LabRig, camera: str) -> float | None:
    """The frame rate `camera` should run at: the frequency of the board trigger whose wiring line (CameraTrigger, DO0
    or DO1) names it in targets, as OpenRigs.Devices CameraTrigger finds it at Start."""
    for board in rig.behavior_board.values():
        trigger = board.camera_trigger_controller
        for output, field in enumerate(("do0", "do1")):
            line = getattr(board.wiring, field, None) if board.wiring else None
            if trigger is not None and line is not None and line.device == "CameraTrigger" and camera in (line.targets or []):
                return float(trigger.trigger0_frequency if output == 0 else trigger.trigger1_frequency)
    return None


def device_verdict(device: Planned, measures: dict | None, limits: dict, rig: LabRig) -> str:
    """"" when the device passed, else why not, as a sentence fragment."""
    if not measures:
        return "did not report"
    if "error" in measures:
        return measures["error"][1] or measures["error"][0] or "failed"
    if "minFrameRate" in limits:  # frames drawn, as the projector's: judged before any person's word
        frames, seconds = _number(measures, "frames"), _number(measures, "seconds")
        rate = frames / seconds if frames and seconds else 0.0
        if rate < limits["minFrameRate"]:
            return f"{rate:.1f} frames/s drawn, {limits['minFrameRate']} needed"
    if "minSounds" in limits:  # the test sounds the mixer was sent: judged before any person's word (Pip, 15 Nov 2026)
        short = [f"{name} {_number(measures, f'sounds:{name}') or 0:g}" for name in _speakers(device)
                 if (_number(measures, f"sounds:{name}") or 0) < limits["minSounds"]]
        if short:
            return f"test sounds sent to the mixer: {', '.join(short)}, {limits['minSounds']} each needed"
    if "personConfirms" in limits:
        return "" if _number(measures, "person") == 1 else "not confirmed by the person"
    if "minFrameRate" in limits:
        return ""
    if "minMessages" in limits:
        n = _number(measures, "messages")
        return "" if n is not None and n >= limits["minMessages"] else f"{n or 0:g} messages, {limits['minMessages']} needed"
    if "minRateFraction" in limits:
        frames, seconds, hz = _number(measures, "frames"), _number(measures, "seconds"), _trigger_hz(rig, device.name)
        if not frames or not seconds or not hz:
            return "no frames" if not frames else "no trigger frequency to judge by"
        rate = frames / seconds
        need = limits["minRateFraction"] * hz
        return "" if rate >= need else f"{rate:.1f} frames/s, {need:.1f} needed"
    return f"no threshold for {device.kind}"


def check_commit(clone: Path) -> str:
    """The last commit that changed the check (src, config), never its packages (src/Extensions.csproj): what
    app-rig-control's gate compares, so the lab's package bumps leave a pass standing; "" without git."""
    try:
        done = subprocess.run(["git", "-C", str(clone), "log", "-1", "--format=%H", "--", "src", "config",
                               ":(exclude)src/Extensions.csproj"],
                              capture_output=True, text=True, timeout=20)
    except (OSError, subprocess.TimeoutExpired):
        return ""
    return done.stdout.strip() if done.returncode == 0 else ""


def judge(rig_yml: Path, raw: Path, thresholds: Path, rig_name: str, commit: str) -> dict:
    """The pass record: what the app-rig-control gate reads. Dated by the raw file, when it was measured, so a
    file left from an earlier run is never a fresh pass."""
    rig = load(rig_yml)
    limits = yaml.safe_load(Path(thresholds).read_text(encoding="utf-8")) or {}
    rows = read_raw(raw)
    planned = plan(rig)
    devices = []
    for device in planned:
        why = device_verdict(device, rows.get(device.name), limits.get(device.kind) or {}, rig)
        devices.append({"device": device.name, "ok": not why, "why": why})
    for extra in sorted(set(rows) - {d.name for d in planned}):
        devices.append({"device": extra, "ok": False, "why": "checked, but rig.yml does not declare it"})
    return {
        "rig": rig_name,
        "at": datetime.fromtimestamp(Path(raw).stat().st_mtime, timezone.utc).isoformat(timespec="seconds"),
        "passed": bool(devices) and all(d["ok"] for d in devices),
        "rigYmlSha256": hashlib.sha256(Path(rig_yml).read_bytes()).hexdigest(),
        "checkCommit": commit,
        "devices": devices,
    }


def write(record: dict, out: Path) -> None:
    out.parent.mkdir(parents=True, exist_ok=True)
    tmp = out.with_suffix(".tmp")
    tmp.write_text(json.dumps(record, indent=2), encoding="utf-8")
    tmp.replace(out)
