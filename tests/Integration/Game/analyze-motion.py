"""Measure two Steam clients' final rendered actor samples on the same Windows QPC clock."""
import argparse
import bisect
import csv
import json
import math
from pathlib import Path


def read(path):
    with path.open(encoding="utf-8-sig", newline="") as stream:
        rows = list(csv.DictReader(stream))
    if not rows:
        raise ValueError(f"No motion samples in {path}")
    numbers = [{k: float(v) for k, v in row.items()} for row in rows]
    return [r for r in numbers if r["world_running"] and r["in_view"]]


def spread(values):
    if not values:
        return {"count": 0}
    values = sorted(values)
    return {"count": len(values), **{
        name: round(values[math.ceil(len(values) * quantile) - 1], 2)
        for name, quantile in (("p50", .5), ("p95", .95), ("max", 1))}}


def dist(a, b):
    return math.hypot(a["render_x"] - b["render_x"], a["render_y"] - b["render_y"])


def different_frame(a, b):
    return a["gpu_frame"] != b["gpu_frame"] or a["asset"] != b["asset"]


def cadence(rows):
    frame_times = [b["qpc"] for a, b in zip(rows, rows[1:]) if different_frame(a, b)]
    hz = rows[0]["qpc_hz"]
    return {"frames_drawn": len(rows), "animation_frame_transitions": len(frame_times),
            "interval_between_animation_changes_ms": spread(
                [(b - a) * 1000 / hz for a, b in zip(frame_times, frame_times[1:])])}


def effects(folder, hz):
    paths = [folder / role / "dig-fx.csv" for role in ("host", "guest")]
    if not all(path.exists() for path in paths):
        return None
    with paths[0].open(encoding="utf-8-sig", newline="") as stream:
        host = list(csv.DictReader(stream))
    with paths[1].open(encoding="utf-8-sig", newline="") as stream:
        guest = list(csv.DictReader(stream))
    result = {}
    for kind in ("contact", "beam"):
        native = [r for r in host if r["kind"] == kind]
        remote = [r for r in guest if r["kind"] == kind]
        samples = [r for r in host if r["kind"] == kind + "_sample"] or native
        keys = ("tick", "cell", "file", "anim")
        key = lambda r: tuple(r[field] for field in keys)
        lookup = {key(r): r for r in samples}
        first = {}
        for r in remote:
            if key(r) in lookup:
                first.setdefault(key(r), r)
        geom = ("root_x", "root_y", "m00", "m01", "m02", "m10", "m11", "m12")
        errors = [max(abs(float(r[field]) - float(lookup[k][field])) for field in geom)
                  for k, r in first.items()]
        result[kind] = {
            "host_rendered_frames": len(native), "guest_rendered_frames": len(remote),
            "same_tick_cell_asset_action_samples": len(first),
            "first_render_with_matching_gpu_animation_frame": sum(r["frame"] == lookup[k]["frame"]
                                                                  for k, r in first.items()),
            "first_render_geometry_max_error": round(max(errors), 5) if errors else None,
            "guest_observed_span_ms": round((float(remote[-1]["qpc"]) - float(remote[0]["qpc"])) * 1000 / hz, 2) if remote else 0,
        }
    return result


def analyze(folder, independent=False):
    host = read(folder / "host" / "dupe-motion.csv")
    guest = read(folder / "guest" / "dupe-motion.csv")
    actors = {r["id"] for r in host + guest}
    rates = {r["qpc_hz"] for r in host + guest}
    if len(actors) != 1 or len(rates) != 1:
        raise ValueError("A stable ID and a shared QPC clock are required")
    hz = rates.pop()
    if independent:
        return {"id": int(actors.pop()), "host": cadence(host), "guest": cadence(guest),
                "timebase": "role-local cadence; no cross-process latency calculated"}
    start = max(host[0]["qpc"], guest[0]["qpc"])
    end = min(host[-1]["qpc"], guest[-1]["qpc"])
    if end - start < 15 * hz:
        raise ValueError("Visible unpaused overlap shorter than 15 seconds")
    times = [r["qpc"] for r in host]
    stamps = {r["pose_tick"]: r["sample_qpc"] for r in host if r["sample_qpc"] > 0}

    def inspect(rows):
        move_ms = move_hold = anim_ms = anim_hold = hold = longest = 0
        durations = []
        for a, b in zip(rows, rows[1:]):
            if a["qpc"] < start or b["qpc"] > end:
                continue
            dt = (b["qpc"] - a["qpc"]) * 1000 / hz
            durations.append(dt)
            mid = (a["qpc"] + b["qpc"]) / 2
            lo = max(0, bisect.bisect_left(times, mid - .080 * hz) - 1)
            hi = min(len(host) - 1, bisect.bisect_left(times, mid + .080 * hz))
            # Evaluate the same wall-clock windows on both roles. Purposeful standing
            # and static animations are excluded from their respective denominators.
            moving = dist(host[lo], host[hi]) > .015
            animating = any(different_frame(host[i - 1], host[i]) for i in range(lo + 1, hi + 1))
            if moving:
                move_ms += dt
                if dist(a, b) < .001:
                    move_hold += dt
                    hold += dt
                    longest = max(longest, hold)
                else:
                    hold = 0
            else:
                hold = 0
            if animating:
                anim_ms += dt
                if not different_frame(a, b):
                    anim_hold += dt
        return {**cadence(rows), "render_interval_ms": spread(durations), "host_motion_window_s": round(move_ms / 1000, 2),
                "position_held_pct_while_host_moves": round(100 * move_hold / move_ms, 2) if move_ms else None,
                "max_unchanged_position_ms_while_host_moves": round(longest, 2),
                "animation_window_s": round(anim_ms / 1000, 2),
                "frame_held_pct_while_host_animates": round(100 * anim_hold / anim_ms, 2) if anim_ms else None}

    first = {}
    responding = {}
    for prev, r in zip(guest, guest[1:]):
        stamp = stamps.get(r["pose_tick"])
        if stamp is None or not start <= stamp <= r["qpc"] <= end:
            continue
        latency = (r["qpc"] - stamp) * 1000 / hz
        first.setdefault(r["pose_tick"], latency)
        if dist(prev, r) >= .001 or different_frame(prev, r):
            responding.setdefault(r["pose_tick"], latency)
    if not first:
        raise ValueError("No rendered guest sample refers to a recorded authority capture")
    return {"id": int(actors.pop()), "visible_unpaused_overlap_sec": round((end - start) / hz, 2),
            "host": inspect(host), "guest": inspect(guest), "digging_effects": effects(folder, hz),
            "capture_to_first_render_using_tick_ms": spread(first.values()),
            "capture_to_first_changed_render_using_tick_ms": spread(responding.values())}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("run", type=Path)
    parser.add_argument("--independent", action="store_true",
                        help="Compare role-local render cadence only (older fixtures with Mono Stopwatch clocks)")
    args = parser.parse_args()
    print(json.dumps(analyze(args.run, args.independent), indent=2, allow_nan=False))
