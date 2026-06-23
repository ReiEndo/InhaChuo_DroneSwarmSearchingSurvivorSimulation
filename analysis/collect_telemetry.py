#!/usr/bin/env python3
from __future__ import annotations

import argparse
import datetime as dt
import os
import platform
import shutil
from pathlib import Path


def unity_persistent_candidates(company: str, product: str) -> list[Path]:
    home = Path.home()
    system = platform.system().lower()
    candidates: list[Path] = []

    if system == "darwin":
        candidates.append(home / "Library" / "Application Support" / company / product / "DroneTelemetry")
    elif system == "windows":
        local_low = os.environ.get("LOCALAPPDATA", "")
        if local_low:
            candidates.append(Path(local_low).parent / "LocalLow" / company / product / "DroneTelemetry")
        appdata = os.environ.get("APPDATA", "")
        if appdata:
            candidates.append(Path(appdata).parent / "LocalLow" / company / product / "DroneTelemetry")
    else:
        xdg = Path(os.environ.get("XDG_CONFIG_HOME", home / ".config"))
        candidates.append(xdg / "unity3d" / company / product / "DroneTelemetry")

    candidates.extend([
        Path.cwd() / "DroneTelemetry",
        Path.cwd() / "data" / "raw",
    ])
    return candidates


def find_source(company: str, product: str) -> Path:
    for candidate in unity_persistent_candidates(company, product):
        if (candidate / "session_summary.csv").exists() or (candidate / "session_events.csv").exists():
            return candidate
    searched = "\n".join(f"  - {p}" for p in unity_persistent_candidates(company, product))
    raise SystemExit(f"No telemetry CSVs found. Searched:\n{searched}\n\nPass --source /path/to/DroneTelemetry if needed.")


def main() -> None:
    parser = argparse.ArgumentParser(description="Copy Unity DroneTelemetry CSVs into the repo.")
    parser.add_argument("--company", default="DefaultCompany", help="Unity PlayerSettings companyName")
    parser.add_argument("--product", default="My project (3)", help="Unity PlayerSettings productName")
    parser.add_argument("--source", type=Path, help="Explicit DroneTelemetry folder")
    parser.add_argument("--out", type=Path, default=Path("data") / "telemetry", help="Destination folder")
    parser.add_argument("--snapshot", action="store_true", help="Write to a timestamped subfolder")
    args = parser.parse_args()

    source = args.source or find_source(args.company, args.product)
    dest = args.out / dt.datetime.now(dt.UTC).strftime("%Y%m%dT%H%M%SZ") if args.snapshot else args.out
    dest.mkdir(parents=True, exist_ok=True)

    copied = []
    for name in ("session_summary.csv", "session_events.csv"):
        src = source / name
        if src.exists():
            shutil.copy2(src, dest / name)
            copied.append(name)

    if not copied:
        raise SystemExit(f"No session_summary.csv/session_events.csv in {source}")

    print(f"Copied {', '.join(copied)}")
    print(f"from: {source}")
    print(f"to:   {dest}")


if __name__ == "__main__":
    main()
