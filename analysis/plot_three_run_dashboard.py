#!/usr/bin/env python3
from __future__ import annotations

import argparse
from pathlib import Path

import pandas as pd

from analyze_telemetry import save_dashboard


def main() -> None:
    parser = argparse.ArgumentParser(description="Create analysis/output/plots/dashboard.png")
    parser.add_argument("--input", type=Path, default=Path("analysis") / "output" / "tables" / "summary_with_derived_metrics.csv")
    parser.add_argument("--out", type=Path, default=Path("analysis") / "output" / "plots")
    args = parser.parse_args()

    if not args.input.exists():
        raise SystemExit(f"Missing {args.input}. Run stat-analyze first.")
    df = pd.read_csv(args.input)
    save_dashboard(df, args.out)
    print(args.out / "dashboard.png")


if __name__ == "__main__":
    main()
