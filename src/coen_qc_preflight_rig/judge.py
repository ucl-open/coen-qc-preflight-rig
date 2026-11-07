"""Whether a rig passed: the workflow's raw measurements against config/thresholds.yml.

The workflow writes one row per measurement, `Device,Measure,Value,Detail`: a device that failed to open
writes `error` with the message; a Harp board `messages` and `seconds`; a camera `frames` and `seconds`;
a section a person confirms `person` (1 yes, 0 no). Every device rig.yml declares must report, and
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
from coen_qc_preflight_rig.rig import CheckedRig


def read_raw(path: Path) -> dict[str, dict[str, tuple[str, str]]]:
    """{device: {measure: (value, detail)}}; a later row for the same measure wins."""
    rows: dict[str, dict[str, tuple[str, str]]] = {}
    with open(path, newline="", encoding="utf-8-sig") as f:
        for row in csv.DictReader(f):
            rows.setdefault(row["Device"], {})[row["Measure"]] = (row.get("Value") or "", row.get("Detail") or "")
    return rows


def _number(measures: dict, key: str) -> float | None:
    try:
        return float(measures[key][0])
    except (KeyError, ValueError):
        return None


def _trigger_hz(rig: CheckedRig) -> float | None:
    """The first behavior board's trigger0Frequency: the frame rate a triggered camera should run at."""
    for board in rig.behavior_board.values():
        trigger = getattr(board, "camera_trigger_controller", None)
        if trigger is not None and getattr(trigger, "trigger0_frequency", None):
            return float(trigger.trigger0_frequency)
    return None


def device_verdict(device: Planned, measures: dict | None, limits: dict, rig: CheckedRig) -> str:
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
    if "personConfirms" in limits:
        return "" if _number(measures, "person") == 1 else "not confirmed by the person"
    if "minFrameRate" in limits:
        return ""
    if "minMessages" in limits:
        n = _number(measures, "messages")
        return "" if n is not None and n >= limits["minMessages"] else f"{n or 0:g} messages, {limits['minMessages']} needed"
    if "minRateFraction" in limits:
        frames, seconds, hz = _number(measures, "frames"), _number(measures, "seconds"), _trigger_hz(rig)
        if not frames or not seconds or not hz:
            return "no frames" if not frames else "no trigger frequency to judge by"
        rate = frames / seconds
        need = limits["minRateFraction"] * hz
        return "" if rate >= need else f"{rate:.1f} frames/s, {need:.1f} needed"
    return f"no threshold for {device.kind}"


def check_commit(clone: Path) -> str:
    """The last commit that changed the check (src, config), never its packages: what rig-control's gate
    compares, so the lab's package bumps leave a pass standing (audit, 7 Nov 2026); "" without git."""
    try:
        done = subprocess.run(["git", "-C", str(clone), "log", "-1", "--format=%H", "--", "src", "config"],
                              capture_output=True, text=True, timeout=20)
    except (OSError, subprocess.TimeoutExpired):
        return ""
    return done.stdout.strip() if done.returncode == 0 else ""


def judge(rig_yml: Path, raw: Path, thresholds: Path, rig_name: str, commit: str) -> dict:
    """The pass record: what the rig-control gate reads. Dated by the raw file, when it was measured, so a
    file left from an earlier run is never a fresh pass (audit, 7 Nov 2026)."""
    rig = CheckedRig.model_validate(yaml.safe_load(Path(rig_yml).read_text(encoding="utf-8-sig")) or {})
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
