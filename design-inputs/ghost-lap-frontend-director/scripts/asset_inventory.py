#!/usr/bin/env python3
"""Read-only audit of Ghost Lap source assets for a coding agent.

Usage: python3 scripts/asset_inventory.py --root "$HOME/Desktop/the-sands-of-time-phase1/design-inputs/game-assets"
Outputs statuses and (when available) dimensions/durations. It NEVER alters files.
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import struct
import subprocess
import sys
import wave

EXPECTED_FILES = [
    "characters.md",
    "backgrounds/snow_arena_master.png",
    "backgrounds/snow_arena.webp",
    "haunted_candle.png",
    "smoke_transition.png",
    "sounds/ghost_main_theme.wav",
    "sounds/multiplayer_theme.wav",
    "sounds/gameLoading.wav",
    "sounds/candle_eat.wav",
    "sounds/player_hit.wav",
    "sounds/blast.wav",
    "sounds/menu.wav",
    "sounds/Gushy_wind.mp3",
    "sounds/Ghost_knocking.wav",
]
EXPECTED_DIRS = [
    "backgrounds",
    "kenney_pixel-platformer-blocks",
    "kenney_shape-characters",
    "sounds",
]


def image_metadata(path: Path) -> dict:
    result = {}
    try:
        if path.suffix.lower() == ".png":
            with path.open("rb") as f:
                hdr = f.read(26)
            if hdr[:8] == b"\x89PNG\r\n\x1a\n" and hdr[12:16] == b"IHDR":
                w, h = struct.unpack(">II", hdr[16:24])
                result.update(width=w, height=h, png_color_type=hdr[25])
        else:
            try:
                from PIL import Image  # optional only
                with Image.open(path) as im:
                    result.update(width=im.width, height=im.height, mode=im.mode)
            except ImportError:
                result["dimension_note"] = "Pillow not installed; optional metadata unavailable"
    except (OSError, ValueError) as exc:
        result["read_error"] = str(exc)
    if result.get("width", 0) > 4096 or result.get("height", 0) > 4096:
        result["atlas_warning"] = "May exceed common GPU texture limits; inspect MAX_TEXTURE_SIZE and repack"
    return result


def audio_metadata(path: Path) -> dict:
    if path.suffix.lower() == ".wav":
        try:
            with wave.open(str(path), "rb") as f:
                return {
                    "duration_seconds": round(f.getnframes() / f.getframerate(), 3) if f.getframerate() else None,
                    "channels": f.getnchannels(),
                    "sample_rate_hz": f.getframerate(),
                    "sample_width_bytes": f.getsampwidth(),
                }
        except (wave.Error, OSError):
            pass
    try:
        result = subprocess.run(
            ["ffprobe", "-v", "error", "-show_entries", "format=duration", "-of", "json", str(path)],
            capture_output=True, text=True, timeout=5, check=False,
        )
        if result.returncode == 0:
            duration = json.loads(result.stdout).get("format", {}).get("duration")
            return {"duration_seconds": round(float(duration), 3)} if duration else {}
    except (OSError, subprocess.TimeoutExpired, ValueError, KeyError):
        pass
    return {"audio_note": "Could not inspect duration (ffprobe optional)"}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, required=True, help="source game-assets directory")
    parser.add_argument("--json", action="store_true", help="JSON output instead of human summary")
    args = parser.parse_args()
    root = args.root.expanduser().resolve()
    if not root.is_dir():
        print(f"ERROR: source assets not accessible: {root}", file=sys.stderr)
        return 2

    report: dict = {"root": str(root), "directories": {}, "approved_files": {}, "other_audio_candidates": []}
    for rel in EXPECTED_DIRS:
        p = root / rel
        report["directories"][rel] = {"exists": p.is_dir(), "files": sum(f.is_file() for f in p.rglob("*")) if p.is_dir() else 0}
    for rel in EXPECTED_FILES:
        p = root / rel
        entry = {"exists": p.is_file()}
        if p.is_file():
            entry["bytes"] = p.stat().st_size
            if p.suffix.lower() in (".png", ".webp", ".jpg", ".jpeg"):
                entry.update(image_metadata(p))
            if p.suffix.lower() in (".wav", ".mp3"):
                entry.update(audio_metadata(p))
        report["approved_files"][rel] = entry
    sounds = root / "sounds"
    if sounds.is_dir():
        approved_names = {Path(p).name for p in EXPECTED_FILES if p.startswith("sounds/")}
        report["other_audio_candidates"] = sorted(
            f.name for f in sounds.iterdir() if f.is_file() and f.suffix.lower() in (".wav", ".mp3", ".ogg") and f.name not in approved_names
        )

    if args.json:
        print(json.dumps(report, indent=2))
    else:
        print(f"Source root: {root}")
        for rel, item in report["directories"].items():
            print(f"{'OK' if item['exists'] else 'MISSING'} folder: {rel} ({item['files']} files)")
        for rel, item in report["approved_files"].items():
            status = "OK" if item["exists"] else "MISSING"
            print(f"{status} {rel} — " + ", ".join(f"{k}={v}" for k, v in item.items() if k != "exists"))
        print(f"Unapproved/undecided sounds (do not assign automatically): {len(report['other_audio_candidates'])}")
        for name in report["other_audio_candidates"]:
            print(f"  • {name}")
    missing = [p for p, value in report["approved_files"].items() if not value["exists"]]
    missing += [p for p, value in report["directories"].items() if not value["exists"]]
    return 1 if missing else 0


if __name__ == "__main__":
    raise SystemExit(main())
