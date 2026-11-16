"""Each rig kind's preflight against the lab's rules for a workflow's hardware (open_rigs.rigs.workflow_lint): the domes'
(src\\main.bonsai, DomeHardware) and the behaviour boxes' (src\\box.bonsai, BBoxHardware), each its rig list, the lab's
check (OpenRigs.Devices PreflightChecks) and its task file, never a device opened by hand (Pip, 16 Nov 2026)."""

import re
import xml.etree.ElementTree as ET
from pathlib import Path

import pytest
import yaml
from open_rigs.rigs import workflow_lint as lint

ROOT = Path(__file__).parents[1]
W, XSI = lint.W, lint.XSI
DOME = ["TimestampGeneratorGen3-Main", "BehaviorBoard-Main", "LicketySplit-Left", "LicketySplit-Right",
        "StepperDriver-A", "StepperDriver-B", "Camera-Face", "Camera-Pupil", "Camera-Body"]
# Each workflow: its rig list, the devices it drives, what it taps (the pieces' data and the projector's frames), its task file.
KINDS = {
    "dome.bonsai": ("DomeHardware", DOME, DOME + ["ProjectionCalibration"], "task-dome.yml"),
    "box.bonsai": ("BBoxHardware", ["BehaviorBoard-Main", "Camera-Overhead"], ["BehaviorBoard-Main", "Camera-Overhead"], "task-box.yml"),
}


@pytest.fixture(params=KINDS, scope="module")
def wf(request):
    return lint.Workflow(ROOT, request.param)


def test_the_hardware_is_the_rig_kind_s_list(wf):
    rig_list, devices, _, _ = KINDS[wf.file]
    assert lint.hardware_problems(wf) == []
    assert f'Path="{lint.DEVICES}{rig_list}.bonsai"' in wf.text
    assert [name for name, _ in lint.required_devices(wf)] == devices


def test_no_device_is_opened_by_hand(wf):
    # The pieces open every device (Pip, 16 Nov 2026); the preflight's own C# only writes into their Commands subjects.
    assert not re.search(r'xsi:type="(harp|beh|vid|spk|aud):(Device|TriggeredSpinnaker|SpinnakerCapture)"', wf.text)
    assert "RetryPortOpen" not in wf.text and "BoardWrites" not in wf.text and 'Path="OpenRigs.Audio:SpeakerMixer.bonsai"' not in wf.text


def test_every_device_is_counted_from_its_piece_s_data(wf):
    _, _, tapped, _ = KINDS[wf.file]
    nodes, edges = lint.nodes_and_edges(next(e for e in wf.root.iter(W + "Expression") if e.findtext(W + "Name") == "Taps").find(W + "Workflow"))
    taps = [i for i, n in enumerate(nodes) if (n.find(W + "Combinator") is not None and n.find(W + "Combinator").get(XSI) == "dev:PreflightTap")]
    feeds = {b: a for a, b, _ in edges}
    out = {a: b for a, b, _ in edges}
    found = []
    for i in taps:
        device = nodes[i].find(W + "Combinator").findtext("{clr-namespace:OpenRigs.Devices;assembly=OpenRigs.Devices}Device")
        source = nodes[feeds[i]].findtext(W + "Name")
        assert source == ("DrawStimuli" if device == "ProjectionCalibration" else f"{device}.Data")
        assert (nodes[out[i]].get(XSI), nodes[out[i]].findtext(W + "Name")) == ("MulticastSubject", "DeviceTicks")
        found.append(device)
    assert found == tapped


def test_the_check_is_the_lab_s_with_no_experiment_folder(wf):
    assert wf.text.count(f'Path="{lint.PREFLIGHT_CHECKS}"') == 1
    assert lint.no_experiment_folder_problems(wf) == []
    assert lint.undeclared_commands(wf) == [] and lint.declared_twice(wf) == []
    assert ("PreflightChecks", "command", "Confirm") in lint.control_declarations(wf)


def test_its_task_file_is_the_rig_kind_s(wf):
    _, _, _, task = KINDS[wf.file]
    (checks,) = [e for e in wf.root.iter(W + "Expression") if e.get("Path") == lint.PREFLIGHT_CHECKS]
    assert checks.findtext(W + "TaskPath") == f"..\\config\\{task}"
    doc = yaml.safe_load((ROOT / "config" / task).read_text(encoding="utf-8"))
    assert set(doc) == {"countSeconds", "questions"} and 1 <= doc["countSeconds"] <= 60
    thresholds = yaml.safe_load((ROOT / "config" / "thresholds.yml").read_text(encoding="utf-8"))
    # A kind a person is asked about is a kind the judge holds to the person's answer, and only on the domes.
    assert set(doc["questions"]) == ({k for k, v in thresholds.items() if v.get("personConfirms")} if wf.file == "dome.bonsai" else set())


def test_a_stop_ends_headless_bonsai_even_when_its_teardown_is_held_up(wf):
    # GREEN-DOME-2, 15 Nov 2026: End Testing's stop at the speaker question left Bonsai running until the agent's kill
    # 30 s later. EndAfterStop ends it 2 s after the stop, a source of its own.
    assert wf.text.count("p1:EndAfterStop") == 1 and "<p1:GraceSeconds>2</p1:GraceSeconds>" in wf.text


def test_each_speaker_s_test_sound_is_counted_into_the_check():
    # Pip, 15 Nov 2026: evidence the speakers played. Each sound past the rig list's SpeakerMixer (MixerStimulusSounds)
    # is counted per rig.yml speaker into PreflightChecks' SpeakerSounds; each speaker's sound at the lab's default level.
    wf = lint.Workflow(ROOT, "dome.bonsai")
    group = next(e for e in wf.root.iter(W + "Expression") if e.findtext(W + "Name") == "SpeakerTest")
    nodes, edges = lint.nodes_and_edges(group.find(W + "Workflow"))
    (count,) = [i for i, n in enumerate(nodes) if n.find(W + "Combinator") is not None and n.find(W + "Combinator").get(XSI) == "p1:CountSpeakerSounds"]
    fed = {label: nodes[a].findtext(W + "Name") for a, b, label in edges if b == count}
    assert fed == {"Source1": "MixerStimulusSounds", "Source2": "RigConfigs"}
    assert [nodes[b].findtext(W + "Name") for a, b, _ in edges if a == count] == ["SpeakerSounds"]
    text = ET.tostring(group, encoding="unicode")
    assert text.count(":SpeakerBuffer") == 1 and "LevelDbfs>-30<" in text
    assert 'Prompt.StartsWith("SpeakerArray:")' in text and "Name>Confirms</" in text


def _top(wf):
    return lint.nodes_and_edges(wf.root.find(W + "Workflow"))


def test_the_rig_list_is_in_a_hardware_group_as_in_every_task(wf):
    rig_list = KINDS[wf.file][0]
    nodes, _ = _top(wf)
    (hardware,) = [n for n in nodes if n.findtext(W + "Name") == "Hardware"]
    assert f'Path="{lint.DEVICES}{rig_list}.bonsai"' in ET.tostring(hardware, encoding="unicode")
    assert not any(n.get("Path", "").endswith(f"{rig_list}.bonsai") for n in nodes)


def test_live_view_is_last_and_fed_by_nothing(wf):
    # StreamDevices finds only subjects built before it: an edge into it (even a ConfigRoot mapping) makes Bonsai build
    # it before the pieces, and it streams nothing.
    nodes, edges = _top(wf)
    (live,) = [i for i, n in enumerate(nodes) if n.get(XSI) == "lv:StreamDevices"]
    assert live == len(nodes) - 1 and not [e for e in edges if live in e[:2]]


def test_the_spouts_move_by_the_stage_s_own_commands():
    # The lick spout stage declares and takes its own commands (SpoutsHome, nudges); the preflight writes no stepper
    # register by hand (its Move and Home are gone).
    text = (ROOT / "src" / "dome.bonsai").read_text(encoding="utf-8-sig")
    assert "StepperCommands" not in text and "StepperDriver-A.Commands" not in text
    assert ("Initialization", "command", "Move") not in lint.control_declarations(lint.Workflow(ROOT, "dome.bonsai"))
    assert "class StepperCommands" not in (ROOT / "src" / "Extensions" / "Preflight.cs").read_text(encoding="utf-8-sig")


def test_the_test_pattern_fills_the_projector_view():
    # ProjectorDisplay's view is azimuth -135 to 135 and elevation -22.5 to 82.5 degrees; the question asks whether the
    # pattern fills the screen, so it is that whole view (it was 120 by 60).
    wf = lint.Workflow(ROOT, "dome.bonsai")
    (quad,) = [e for e in wf.root.iter(W + "Expression") if e.get("Path") == "BonVision:Primitives.DrawQuad.bonsai"]
    got = [float(quad.findtext(W + k)) for k in ("ExtentX", "ExtentY", "LocationX", "LocationY")]
    assert got == [135 - -135, 82.5 - -22.5, 0, (82.5 + -22.5) / 2]


def test_pulse_and_reset_are_the_board_piece_s(wf):
    # lib's BehaviorBoard.ManualPulses declares and writes them (every pulse timed by the board; its tests are lib's
    # PulseCommandsTests), so neither preflight writes board registers by hand.
    nodes, _ = _top(wf)
    (hardware,) = [n for n in nodes if n.findtext(W + "Name") == "Hardware"]
    assert ET.tostring(hardware, encoding="unicode").count('Path="OpenRigs.Devices:BehaviorBoard.ManualPulses.bonsai"') == 1
    assert "p1:BoardCommands" not in wf.text and "class BoardCommands" not in (ROOT / "src" / "Extensions" / "Preflight.cs").read_text(encoding="utf-8-sig")
