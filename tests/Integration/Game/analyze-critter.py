"""Compare actual in-view movement for the same critter in a Steam dual run."""
import argparse
import csv
from math import dist
from pathlib import Path


def summarize(path):
    with path.open(encoding="utf-8-sig", newline="") as stream:
        rows = list(csv.DictReader(stream))
    ids = {r["id"] for r in rows}
    if len(ids) != 1:
        raise ValueError(f"{path} must contain exactly one observed critter")
    steps = []
    for a, b in zip(rows, rows[1:]):
        if a["visible"] != "1" or b["visible"] != "1":
            continue
        step = dist([float(a["gpu_x"]), float(a["gpu_y"])],
                    [float(b["gpu_x"]), float(b["gpu_y"])])
        if step > 0.0001:
            steps.append(step)
    if not steps:
        raise ValueError(f"No visible movement in {path}")
    intermediate = sum(r["animated_between_packets"] == "1" for r in rows)
    steps.sort()
    return ids.pop(), len(steps), intermediate, steps[int(0.95 * (len(steps)-1))], steps[-1]


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("run", type=Path)
    args = parser.parse_args()
    results = [summarize(args.run / role / "critter-motion.csv") for role in ("host", "guest")]
    if results[0][0] != results[1][0]:
        raise ValueError("Host and guest observed different animals")
    print("role,id,moving_frames,between_packets,p95_step_cells,max_step_cells")
    for role, (identity, count, intermediate, p95, maximum) in zip(("host", "guest"), results):
        print(f"{role},{identity},{count},{intermediate},{p95:.4f},{maximum:.4f}")
