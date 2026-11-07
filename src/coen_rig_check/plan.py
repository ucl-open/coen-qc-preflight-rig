"""Which devices a rig's check covers: one per keyed entry in rig.yml, named as lib-open-rigs names it."""

from dataclasses import dataclass

from coen_rig_check.rig import DEVICE_KINDS, SINGLE_SECTIONS, CheckedRig

# rig.yml key to CheckedRig attribute.
_FIELDS = {
    "behaviorBoard": "behavior_board",
    "timestampGeneratorGen3": "timestamp_generator_gen3",
    "stepperDriver": "stepper_driver",
    "licketySplit": "lickety_split",
    "camera": "camera",
    "speakerArray": "speaker_array",
    "projectionCalibration": "projection_calibration",
}


@dataclass(frozen=True)
class Planned:
    kind: str  # the rig.yml key, such as camera
    instance: str  # the entry's key, such as body; "" for a single section
    settings: object  # the entry's pydantic model

    @property
    def name(self) -> str:
        """Camera-Body, as lib-open-rigs' RigDevices names it; SpeakerArray for a single section."""
        pascal = lambda s: s[:1].upper() + s[1:]  # noqa: E731
        return pascal(self.kind) + ("-" + pascal(self.instance) if self.instance else "")


def plan(rig: CheckedRig) -> list[Planned]:
    """Every device and single section the rig declares, in check order."""
    out = [Planned(kind, name, entry) for kind in DEVICE_KINDS for name, entry in getattr(rig, _FIELDS[kind]).items()]
    out += [Planned(kind, "", getattr(rig, _FIELDS[kind])) for kind in SINGLE_SECTIONS if getattr(rig, _FIELDS[kind])]
    return out
