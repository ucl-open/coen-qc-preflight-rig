"""coen-rig-check plan <rig.yml> | judge <rig.yml> <raw.csv> <out.json> --rig NAME --commit SHA."""

import argparse
import json
from pathlib import Path

from coen_rig_check import judge, plan, rig

THRESHOLDS = Path(__file__).resolve().parents[2] / "config" / "thresholds.yml"


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(prog="coen-rig-check")
    sub = p.add_subparsers(dest="cmd", required=True)
    sp = sub.add_parser("plan", help="list the devices a rig.yml's check covers")
    sp.add_argument("rig_yml", type=Path)
    sj = sub.add_parser("judge", help="judge a run's raw measurements and write the pass record")
    sj.add_argument("rig_yml", type=Path)
    sj.add_argument("raw", type=Path)
    sj.add_argument("out", type=Path)
    sj.add_argument("--rig", required=True)
    sj.add_argument("--commit", required=True)
    sj.add_argument("--thresholds", type=Path, default=THRESHOLDS)
    a = p.parse_args(argv)
    if a.cmd == "plan":
        for d in plan.plan(rig.load(a.rig_yml)):
            print(d.name)
        return 0
    record = judge.judge(a.rig_yml, a.raw, a.thresholds, a.rig, a.commit)
    judge.write(record, a.out)
    print(json.dumps(record, indent=2))
    return 0 if record["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
