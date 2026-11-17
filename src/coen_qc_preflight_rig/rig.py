"""Every rig's rig.yml, read by the lab rig model every lab workflow reads it with.

lib-open-rigs' `open_rigs.rigs.lab_rig.LabRig` (in Bonsai, `OpenRigs.Devices:ReadLabRig.bonsai`): each device
kind is a group of named entries and defaults to none, each single section is optional, so a dome's file and a
bbox's both load; a key the model lacks is refused, as every workflow refuses it at Start.
"""

from pathlib import Path

import yaml
from open_rigs.rigs.lab_rig import LabRig

def load(path: Path) -> LabRig:
    """The rig.yml at path; raises pydantic's ValidationError naming the key when a declared device is wrong."""
    data = yaml.safe_load(Path(path).read_text(encoding="utf-8-sig")) or {}
    return LabRig.model_validate(data)
