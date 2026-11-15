"""The lab rig model on real rig.yml files (copied from K:\\config\\machines on 7 Nov 2026), the plan, and the judge."""

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
def test_a_dome_fixture_is_a_valid_lab_rig(name):
    from open_rigs.rigs.lab_rig import LabRig

    LabRig.model_validate(yaml.safe_load((FIXTURES / f"{name}.yml").read_text(encoding="utf-8")))


def test_a_box_plans_its_board_and_camera_only():
    assert [d.name for d in plan.plan(rig.load(FIXTURES / "BBOX-1.yml"))] == ["BehaviorBoard-Main", "Camera-Overhead"]


def test_absent_sections_are_fine(tmp_path):
    f = tmp_path / "rig.yml"
    f.write_text("rootPath: C:\\x\n", encoding="utf-8")
    assert plan.plan(rig.load(f)) == []


def test_a_key_the_lab_rig_model_lacks_is_refused_as_every_workflow_refuses_it(tmp_path):
    f = tmp_path / "rig.yml"
    f.write_text("rootPath: C:\\x\nsomethingNew: { a: 1 }\n", encoding="utf-8")
    with pytest.raises(pydantic.ValidationError, match="somethingNew"):
        rig.load(f)


def test_a_wrong_device_entry_is_refused(tmp_path):
    data = yaml.safe_load((FIXTURES / "BBOX-1.yml").read_text(encoding="utf-8"))
    del data["behaviorBoard"]["main"]["portName"]
    f = tmp_path / "rig.yml"
    f.write_text(yaml.safe_dump(data), encoding="utf-8")
    with pytest.raises(pydantic.ValidationError, match="portName"):
        rig.load(f)


def _raw(tmp_path, rows):
    f = tmp_path / "raw.csv"
    f.write_text("device,measure,value,detail\n" + "".join(",".join(r) + "\n" for r in rows), encoding="utf-8")
    return f


GOOD_BOX = [("BehaviorBoard-Main", "messages", "120", ""), ("BehaviorBoard-Main", "seconds", "5", ""),
            ("Camera-Overhead", "frames", "250", ""), ("Camera-Overhead", "seconds", "5", "")]


def _judge(tmp_path, rows):
    return judge.judge(FIXTURES / "BBOX-1.yml", _raw(tmp_path, rows), THRESHOLDS, "BBOX-1", "abc")


def test_a_box_that_works_passes(tmp_path):
    record = _judge(tmp_path, GOOD_BOX)
    assert record["passed"], record
    assert record["rigYmlSha256"] and record["checkCommit"] == "abc"


def test_a_device_that_did_not_open_fails_with_its_message(tmp_path):
    rows = GOOD_BOX[:2] + [("Camera-Overhead", "error", "", "The camera 23201539 was not found")]
    record = _judge(tmp_path, rows)
    assert not record["passed"]
    assert record["devices"][1] == {"device": "Camera-Overhead", "ok": False, "why": "The camera 23201539 was not found"}


def test_a_device_that_did_not_report_fails(tmp_path):
    assert _judge(tmp_path, GOOD_BOX[:2])["devices"][1]["why"] == "did not report"


def test_a_slow_camera_fails(tmp_path):
    rows = GOOD_BOX[:2] + [("Camera-Overhead", "frames", "200", ""), ("Camera-Overhead", "seconds", "5", "")]
    assert _judge(tmp_path, rows)["devices"][1]["why"] == "40.0 frames/s, 49.0 needed"


def test_a_device_rig_yml_does_not_declare_fails(tmp_path):
    record = _judge(tmp_path, GOOD_BOX + [("StepperDriver-A", "messages", "5", "")])
    assert not record["passed"]
    assert record["devices"][-1]["why"] == "checked, but rig.yml does not declare it"


SOUNDS = {"left": "2", "right": "2", "centre": "2"}  # GREEN-DOME-1's speakers, each sent its test sound twice


def _dome(tmp_path, projection, person="1", sounds=SOUNDS):
    rows = [(d, "messages", "9", "") for d in DOME[:6]] + \
           [(d, m, v, "") for d in DOME[6:9] for m, v in (("frames", "250"), ("seconds", "5"))] + \
           [("SpeakerArray", f"sounds:{s}", n, "") for s, n in sounds.items()] + [("SpeakerArray", "person", person, "")] + \
           [("ProjectionCalibration", m, v, "") for m, v in projection]
    record = judge.judge(FIXTURES / "GREEN-DOME-1.yml", _raw(tmp_path, rows), THRESHOLDS, "GREEN-DOME-1", "abc")
    return {d["device"]: d["why"] for d in record["devices"] if not d["ok"]}


PROJECTED = [("frames", "300"), ("seconds", "5"), ("person", "1")]


def test_a_section_a_person_confirms(tmp_path):
    assert _dome(tmp_path, [("frames", "300"), ("seconds", "5"), ("person", "0")]) == \
        {"ProjectionCalibration": "not confirmed by the person"}
    assert _dome(tmp_path, PROJECTED) == {}


def test_speakers_not_heard_fail(tmp_path):
    assert _dome(tmp_path, PROJECTED, person="0") == {"SpeakerArray": "not confirmed by the person"}


def test_speakers_the_mixer_was_sent_no_test_sound_fail_whatever_the_person_says(tmp_path):
    # Pip, 15 Nov 2026: evidence plus person. The workflow counts the test sounds the mixer is sent for each rig.yml
    # speaker (SpeakerArray,sounds:<speaker>,n); a speaker sent none fails before, and regardless of, the person's Yes.
    none = "test sounds sent to the mixer: left 0, right 0, centre 0, 1 each needed"
    assert _dome(tmp_path, PROJECTED, sounds={"left": "0", "right": "0", "centre": "0"}) == {"SpeakerArray": none}
    assert _dome(tmp_path, PROJECTED, sounds={}) == {"SpeakerArray": none}  # a workflow from before the count
    assert _dome(tmp_path, PROJECTED, sounds={"left": "3", "right": "1"}) == \
        {"SpeakerArray": "test sounds sent to the mixer: centre 0, 1 each needed"}
    # With every speaker sent one, the person's answer decides, as before.
    assert _dome(tmp_path, PROJECTED, sounds={"left": "1", "right": "1", "centre": "1"}) == {}
    assert _dome(tmp_path, PROJECTED, person="0", sounds={"left": "1", "right": "1", "centre": "1"}) == \
        {"SpeakerArray": "not confirmed by the person"}


WORKFLOW = (Path(__file__).parents[1] / "src" / "main.bonsai").read_text(encoding="utf-8-sig")


def test_each_speaker_plays_a_test_sound_through_the_lab_s_mixer_before_the_question():
    # Pip, 15 Nov 2026: each rig.yml speaker plays a test sound through lib's SpeakerMixer at lib's default level
    # (SpeakerBuffer's -30 dBFS RMS), on its channel alone; only a rig with a speakerArray (PreflightDevices Kinds
    # speakerArray, so a box is neither played to nor asked), from the speaker question until its Confirm.
    import xml.etree.ElementTree as ET

    w, xsi = "{https://bonsai-rx.org/2018/workflow}", "{http://www.w3.org/2001/XMLSchema-instance}type"
    root = ET.fromstring(WORKFLOW.encode("utf-8"))
    test = next(e for e in root.iter(w + "Expression") if e.findtext(w + "Name") == "SpeakerTest")
    text = ET.tostring(test, encoding="unicode")
    assert 'Path="OpenRigs.Audio:SpeakerMixer.bonsai"' in text and WORKFLOW.count("OpenRigs.Audio:SpeakerMixer.bonsai") == 1
    assert [k.text for k in test.iter("{clr-namespace:;assembly=Extensions}Kinds")] == ["speakerArray"]
    buffers = [c for c in test.iter(w + "Combinator") if c.get(xsi) == "aud:SpeakerBuffer"]
    assert len(buffers) == 1 and buffers[0].findtext("{clr-namespace:OpenRigs.Audio;assembly=OpenRigs.Audio}LevelDbfs") == "-30"
    assert 'Prompt.StartsWith("SpeakerArray:")' in text and "<p1:Device>SpeakerArray</p1:Device>" in WORKFLOW
    assert "speakerArray=Did each speaker play the test sound?" in WORKFLOW and "<p1:NotOpened>speakerArray</p1:NotOpened>" in WORKFLOW


W = "{https://bonsai-rx.org/2018/workflow}"
XSI = "{http://www.w3.org/2001/XMLSchema-instance}type"


def _scope(name):
    """The nodes of the group or SelectMany called name, each as (kind, name), and its edges as (from, to, label)."""
    import xml.etree.ElementTree as ET

    root = ET.fromstring(WORKFLOW.encode("utf-8"))
    outer = next(e for e in root.iter(W + "Expression") if e.findtext(W + "Name") == name)
    workflow = outer.find(W + "Workflow")
    nodes = []
    for e in workflow.find(W + "Nodes"):
        combinator = e.find(W + "Combinator")
        kind = combinator.get(XSI) if combinator is not None else e.get(XSI)
        nodes.append((kind, e.findtext(W + "Name") or e.findtext("{clr-namespace:Bonsai.Reactive;assembly=Bonsai.Core}Name")))
    edges = [(int(x.get("From")), int(x.get("To")), x.get("Label")) for x in workflow.find(W + "Edges")]
    return nodes, edges


def _feeds(nodes, edges, into_kind, label):
    """The node feeding the input label of the node of kind into_kind."""
    into = next(i for i, n in enumerate(nodes) if n[0] == into_kind)
    return [nodes[f] for f, t, lab in edges if t == into and lab == label]


def test_the_test_sounds_the_mixer_is_sent_are_counted_into_the_metric_and_the_rows():
    # Pip, 15 Nov 2026: evidence the speakers played. Each sound past SpeakerMixer's CheckMixerStarted
    # (MixerStimulusSounds, AppendBuffer's input) is counted per speaker into SpeakerSounds, which the devices
    # metric (DeviceRates) and the raw rows (RunCheck) read.
    nodes, edges = _scope("SpeakerArray")
    assert _feeds(nodes, edges, "p1:CountSpeakerSounds", "Source1") == [("SubscribeSubject", "MixerStimulusSounds")]
    assert _feeds(nodes, edges, "p1:CountSpeakerSounds", "Source2") == [("WorkflowInput", "Source1")]
    count = next(i for i, n in enumerate(nodes) if n[0] == "p1:CountSpeakerSounds")
    assert [nodes[t] for f, t, _ in edges if f == count] == [("MulticastSubject", "SpeakerSounds")]
    assert WORKFLOW.count('TypeArguments="p1:SpeakerSounds"') == 1 and "<rx:Name>SpeakerSounds</rx:Name>" in WORKFLOW
    assert _feeds(*_scope("Ready"), "p1:DeviceRates", "Source3") == [("SubscribeSubject", "SpeakerSounds")]
    assert _feeds(*_scope("Check"), "p1:RunCheck", "Source5") == [("SubscribeSubject", "SpeakerSounds")]


def test_a_stop_ends_headless_bonsai_even_when_its_teardown_is_held_up():
    # GREEN-DOME-2, 15 Nov 2026: End Testing's stop at the speaker question left Bonsai running until the agent's
    # kill 30 s later. EndAfterStop ends it 2 s after the stop.
    nodes, edges = _scope("Initialization")
    guard = [i for i, n in enumerate(nodes) if n[0] == "p1:EndAfterStop"]
    assert len(guard) == 1 and WORKFLOW.count("p1:EndAfterStop") == 1
    assert not [e for e in edges if guard[0] in e[:2]]  # a source of its own, joined to nothing
    assert "<p1:GraceSeconds>2</p1:GraceSeconds>" in WORKFLOW


def test_a_slow_projector_fails_whatever_the_person_says(tmp_path):
    assert _dome(tmp_path, [("frames", "50"), ("seconds", "5"), ("person", "1")]) == \
        {"ProjectionCalibration": "10.0 frames/s drawn, 55 needed"}


def test_the_record_is_dated_by_the_raw_file(tmp_path):
    import os
    from datetime import datetime, timezone

    raw = _raw(tmp_path, GOOD_BOX)
    then = datetime(2026, 11, 6, 9, 0, tzinfo=timezone.utc).timestamp()
    os.utime(raw, (then, then))
    record = judge.judge(FIXTURES / "BBOX-1.yml", raw, THRESHOLDS, "BBOX-1", "abc")
    assert record["at"] == "2026-11-06T09:00:00+00:00"


def test_check_commit_is_the_last_change_to_src_or_config():
    repo = Path(__file__).parents[1]
    import subprocess

    want = subprocess.run(["git", "-C", str(repo), "log", "-1", "--format=%H", "--", "src", "config"],
                          capture_output=True, text=True).stdout.strip()
    assert want and judge.check_commit(repo) == want
    assert judge.check_commit(repo / "no-such-folder") == ""
