"""Every rig's rig.yml, read by one schema.

A superset of the lab's keyed rig.yml layouts (coen-behaviour-box, av-dome): each device kind is a group
of named entries and defaults to none, each single section is optional, and any key not here is ignored
(pydantic's default), so a dome's file and a box's both load. The device classes are open_rigs', never
copies, so a rig.yml the workflows take is one this takes.
"""

from pathlib import Path
from typing import Dict, Optional

import yaml
from open_rigs.components import ProjectionCalibration, SpeakerArray
from open_rigs.core.rig import Rig
from open_rigs.devices import HarpTimestampGeneratorGen3, LicketySplit
from open_rigs.rigs.behaviour_box import BehaviorBoard, HarpStepperDriver, SpinnakerCamera
from pydantic import Field

# The keyed sections, in the order the check runs them, by their rig.yml key.
DEVICE_KINDS = ("behaviorBoard", "timestampGeneratorGen3", "stepperDriver", "licketySplit", "camera")
# The single sections that turn a check on when present.
SINGLE_SECTIONS = ("speakerArray", "projectionCalibration")


class CheckedRig(Rig):
    """The parts of a rig.yml the preflight reads."""

    behavior_board: Dict[str, BehaviorBoard] = Field(default_factory=dict)
    timestamp_generator_gen3: Dict[str, HarpTimestampGeneratorGen3] = Field(default_factory=dict)
    stepper_driver: Dict[str, HarpStepperDriver] = Field(default_factory=dict)
    lickety_split: Dict[str, LicketySplit] = Field(default_factory=dict)
    camera: Dict[str, SpinnakerCamera] = Field(default_factory=dict)
    speaker_array: Optional[SpeakerArray] = None
    projection_calibration: Optional[ProjectionCalibration] = None


def load(path: Path) -> CheckedRig:
    """The rig.yml at path; raises pydantic's ValidationError naming the key when a declared device is wrong."""
    data = yaml.safe_load(Path(path).read_text(encoding="utf-8-sig")) or {}
    return CheckedRig.model_validate(data)
