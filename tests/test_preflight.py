"""The superset schema on real rig.yml files (copied from K:\\config\\machines on 7 Nov 2026), the plan, and the judge."""

from pathlib import Path

import pydantic
import pytest
import yaml

from coen_qc_preflight_rig import judge, plan, rig

FIXTURES = Path(__file__).parent / "fixtures"
THRESHOLDS = Path(__file__).parents[1] / "config" / "thresholds.yml"
DOME = ["BehaviorBoard-Main", "TimestampGeneratorGen3-Main", "StepperDriver-A", "StepperDriver-B",
        "LicketySplit-Left", "LicketySplit-Right", "Camera-Face", "Camera-Pupil", "Camera-Body",
        "SpeakerArray", "ProjectionCalibration"]


@pytest.mark.parametrize("name", ["GREEN-DOME-1", "BLUE-OPTO-1", "BLUE-OPTO-2"])
def test_a_dome_plans_every_device(name):
    assert [d.name for d in plan.plan(rig.load(FIXTURES / f"{name}.yml"))] == DOME


@pytest.mark.parametrize("name", ["GREEN-DOME-1", "BLUE-OPTO-1", "BLUE-OPTO-2"])
def test_a_dome_fixture_is_a_valid_dome_rig(name):
    from open_rigs.rigs.dome import DomeRig

    DomeRig.model_validate(yaml.safe_load((FIXTURES / f"{name}.yml").read_text(encoding="utf-8")))


def test_a_box_plans_its_board_and_camera_only():
    assert [d.name for d in plan.plan(rig.load(FIXTURES / "GEEK-BBOX1.yml"))] == ["BehaviorBoard-Main", "Camera-Box"]


def test_unknown_keys_and_absent_sections_are_fine(tmp_path):
    f = tmp_path / "rig.yml"
    f.write_text("rootPath: C:\\x\nsomethingNew: { a: 1 }\n", encoding="utf-8")
    assert plan.plan(rig.load(f)) == []


def test_a_wrong_device_entry_is_refused(tmp_path):
    data = yaml.safe_load((FIXTURES / "GEEK-BBOX1.yml").read_text(encoding="utf-8"))
    del data["behaviorBoard"]["main"]["portName"]
    f = tmp_path / "rig.yml"
    f.write_text(yaml.safe_dump(data), encoding="utf-8")
    with pytest.raises(pydantic.ValidationError, match="portName"):
        rig.load(f)


def _raw(tmp_path, rows):
    f = tmp_path / "raw.csv"
    f.write_text("Device,Measure,Value,Detail\n" + "".join(",".join(r) + "\n" for r in rows), encoding="utf-8")
    return f


GOOD_BOX = [("BehaviorBoard-Main", "messages", "120", ""), ("BehaviorBoard-Main", "seconds", "5", ""),
            ("Camera-Box", "frames", "250", ""), ("Camera-Box", "seconds", "5", "")]


def _judge(tmp_path, rows):
    return judge.judge(FIXTURES / "GEEK-BBOX1.yml", _raw(tmp_path, rows), THRESHOLDS, "GEEK-BBOX1", "abc")


def test_a_box_that_works_passes(tmp_path):
    record = _judge(tmp_path, GOOD_BOX)
    assert record["passed"], record
    assert record["rigYmlSha256"] and record["checkCommit"] == "abc"


def test_a_device_that_did_not_open_fails_with_its_message(tmp_path):
    rows = GOOD_BOX[:2] + [("Camera-Box", "error", "", "The camera 23201539 was not found")]
    record = _judge(tmp_path, rows)
    assert not record["passed"]
    assert record["devices"][1] == {"device": "Camera-Box", "ok": False, "why": "The camera 23201539 was not found"}


def test_a_device_that_did_not_report_fails(tmp_path):
    assert _judge(tmp_path, GOOD_BOX[:2])["devices"][1]["why"] == "did not report"


def test_a_slow_camera_fails(tmp_path):
    rows = GOOD_BOX[:2] + [("Camera-Box", "frames", "200", ""), ("Camera-Box", "seconds", "5", "")]
    assert _judge(tmp_path, rows)["devices"][1]["why"] == "40.0 frames/s, 49.0 needed"


def test_a_device_rig_yml_does_not_declare_fails(tmp_path):
    record = _judge(tmp_path, GOOD_BOX + [("StepperDriver-A", "messages", "5", "")])
    assert not record["passed"]
    assert record["devices"][-1]["why"] == "checked, but rig.yml does not declare it"


def _dome(tmp_path, projection):
    rows = [(d, "messages", "9", "") for d in DOME[:6]] + \
           [(d, m, v, "") for d in DOME[6:9] for m, v in (("frames", "250"), ("seconds", "5"))] + \
           [("SpeakerArray", "person", "1", "")] + [("ProjectionCalibration", m, v, "") for m, v in projection]
    record = judge.judge(FIXTURES / "GREEN-DOME-1.yml", _raw(tmp_path, rows), THRESHOLDS, "GREEN-DOME-1", "abc")
    return {d["device"]: d["why"] for d in record["devices"] if not d["ok"]}


def test_a_section_a_person_confirms(tmp_path):
    assert _dome(tmp_path, [("frames", "300"), ("seconds", "5"), ("person", "0")]) == \
        {"ProjectionCalibration": "not confirmed by the person"}
    assert _dome(tmp_path, [("frames", "300"), ("seconds", "5"), ("person", "1")]) == {}


def test_a_slow_projector_fails_whatever_the_person_says(tmp_path):
    assert _dome(tmp_path, [("frames", "50"), ("seconds", "5"), ("person", "1")]) == \
        {"ProjectionCalibration": "10.0 frames/s drawn, 55 needed"}


def test_the_record_is_dated_by_the_raw_file(tmp_path):
    import os
    from datetime import datetime, timezone

    raw = _raw(tmp_path, GOOD_BOX)
    then = datetime(2026, 11, 6, 9, 0, tzinfo=timezone.utc).timestamp()
    os.utime(raw, (then, then))
    record = judge.judge(FIXTURES / "GEEK-BBOX1.yml", raw, THRESHOLDS, "GEEK-BBOX1", "abc")
    assert record["at"] == "2026-11-06T09:00:00+00:00"


def test_check_commit_is_the_last_change_to_src_or_config():
    repo = Path(__file__).parents[1]
    import subprocess

    want = subprocess.run(["git", "-C", str(repo), "log", "-1", "--format=%H", "--", "src", "config"],
                          capture_output=True, text=True).stdout.strip()
    assert want and judge.check_commit(repo) == want
    assert judge.check_commit(repo / "no-such-folder") == ""
