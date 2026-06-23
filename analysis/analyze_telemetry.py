#!/usr/bin/env python3
from __future__ import annotations

import argparse
from pathlib import Path

import matplotlib.pyplot as plt
import numpy as np
import pandas as pd
import seaborn as sns
from scipy import stats

TIMING_COLS = [
    "time_to_find_s",
    "time_to_command_notified_s",
    "time_to_all_drones_informed_s",
    "time_to_mission_complete_s",
    "command_delay_s",
    "swarm_propagation_delay_s",
    "confirmation_delay_s",
]
GROUP_COLS = [
    "planner_type",
    "drone_count",
    "sensor_radius",
    "communication_radius",
    "drone_speed",
]


def to_bool(s: pd.Series) -> pd.Series:
    return s.astype(str).str.lower().isin(["true", "1", "yes"])


def load_summary(input_dir: Path, include_destroyed: bool) -> pd.DataFrame:
    path = input_dir / "session_summary.csv"
    if not path.exists():
        raise SystemExit(f"Missing {path}. Run stat-collect first or pass --input.")
    df = pd.read_csv(path)
    for col in df.columns:
        if (
            col.startswith("time_to_")
            or col.endswith("_radius")
            or col
            in [
                "drone_speed",
                "nearest_drone_start_distance_cells",
                "nearest_drone_start_distance_world",
            ]
        ):
            df[col] = pd.to_numeric(df[col], errors="coerce")
    if "completed" in df:
        df["completed_bool"] = to_bool(df["completed"])
    if not include_destroyed and "end_reason" in df:
        df = df[df["end_reason"].fillna("") != "destroyed"].copy()

    df["command_delay_s"] = df.get("time_to_command_notified_s", np.nan) - df.get(
        "time_to_find_s", np.nan
    )
    df["swarm_propagation_delay_s"] = df.get(
        "time_to_all_drones_informed_s", np.nan
    ) - df.get("time_to_find_s", np.nan)
    df["confirmation_delay_s"] = df.get("time_to_mission_complete_s", np.nan) - df.get(
        "time_to_all_drones_informed_s", np.nan
    )
    df["found_person"] = df.get(
        "time_to_find_s", pd.Series(index=df.index, dtype=float)
    ).notna()
    df["all_drones_informed"] = df.get(
        "time_to_all_drones_informed_s", pd.Series(index=df.index, dtype=float)
    ).notna()
    df["mission_complete"] = df.get(
        "end_reason", pd.Series(index=df.index, dtype=str)
    ).eq("mission_complete")
    return df


def write_tables(df: pd.DataFrame, out: Path) -> None:
    out.mkdir(parents=True, exist_ok=True)
    df.to_csv(out / "summary_with_derived_metrics.csv", index=False)

    present_groups = [c for c in GROUP_COLS if c in df.columns]
    rows = []
    for group in present_groups:
        for value, g in df.groupby(group, dropna=False):
            row = {"group_by": group, "value": value, "n": len(g)}
            row["success_rate"] = g["mission_complete"].mean()
            row["found_rate"] = g["found_person"].mean()
            row["all_drones_informed_rate"] = g["all_drones_informed"].mean()
            for metric in TIMING_COLS:
                if metric in g:
                    values = g[metric].dropna()
                    row[f"{metric}_median"] = values.median() if len(values) else np.nan
                    row[f"{metric}_mean"] = values.mean() if len(values) else np.nan
                    row[f"{metric}_iqr"] = (
                        (values.quantile(0.75) - values.quantile(0.25))
                        if len(values)
                        else np.nan
                    )
            rows.append(row)
    pd.DataFrame(rows).to_csv(out / "grouped_metrics.csv", index=False)

    if "end_reason" in df:
        pd.crosstab(
            df.get("planner_type", "all"), df["end_reason"], margins=True
        ).to_csv(out / "end_reason_counts.csv")

    if {"nearest_drone_start_distance_cells", "time_to_find_s"}.issubset(df.columns):
        clean = df[["nearest_drone_start_distance_cells", "time_to_find_s"]].dropna()
        if len(clean) >= 3:
            r, p = stats.pearsonr(
                clean["nearest_drone_start_distance_cells"], clean["time_to_find_s"]
            )
            pd.DataFrame([{"pearson_r": r, "p_value": p, "n": len(clean)}]).to_csv(
                out / "distance_vs_find_correlation.csv", index=False
            )


def save_dashboard(df: pd.DataFrame, out: Path) -> None:
    out.mkdir(parents=True, exist_ok=True)
    sns.set_theme(style="whitegrid")

    plot_df = df.reset_index(drop=True).copy()
    plot_df["run"] = np.arange(1, len(plot_df) + 1)

    fig, axes = plt.subplots(2, 2, figsize=(12, 8))
    fig.suptitle(
        f"Drone Mission Telemetry Dashboard ({len(plot_df)} runs)",
        fontsize=16,
        weight="bold",
    )

    ax = axes[0, 0]
    for col, label, marker in [
        ("time_to_find_s", "Found person", "o"),
        ("time_to_all_drones_informed_s", "All drones informed", "s"),
        ("time_to_mission_complete_s", "Mission complete", "^"),
    ]:
        if col in plot_df and plot_df[col].notna().any():
            ax.plot(
                plot_df["run"], plot_df[col], marker=marker, linewidth=2, label=label
            )
    ax.set_title("Milestone times by run")
    ax.set_xlabel("Run")
    ax.set_ylabel("Seconds")
    if len(plot_df) <= 30:
        ax.set_xticks(plot_df["run"])
    ax.legend()

    ax = axes[0, 1]
    delay_cols = [
        ("swarm_propagation_delay_s", "Swarm propagation delay"),
        ("confirmation_delay_s", "Confirmation delay"),
    ]
    available = [
        (c, l) for c, l in delay_cols if c in plot_df and plot_df[c].notna().any()
    ]
    x = np.arange(len(plot_df))
    if available:
        width = min(0.8 / len(available), 0.35)
        for i, (col, label) in enumerate(available):
            offset = (i - (len(available) - 1) / 2) * width
            ax.bar(x + offset, plot_df[col], width, label=label)
        ax.set_xticks(x, plot_df["run"] if len(plot_df) <= 30 else [])
        ax.legend()
    else:
        ax.text(
            0.5, 0.5, "No delay data", ha="center", va="center", transform=ax.transAxes
        )
    ax.set_title("Derived delays")
    ax.set_xlabel("Run")
    ax.set_ylabel("Seconds")

    ax = axes[1, 0]
    if "end_reason" in plot_df:
        outcomes = plot_df["end_reason"].fillna("unknown").value_counts()
        colors = [
            "#4c78a8" if k == "mission_complete" else "#f58518" for k in outcomes.index
        ]
        ax.bar(outcomes.index.astype(str), outcomes.values, color=colors)
        for i, v in enumerate(outcomes.values):
            ax.text(i, v + max(outcomes.values) * 0.02, str(v), ha="center")
    else:
        ax.text(
            0.5,
            0.5,
            "No end_reason column",
            ha="center",
            va="center",
            transform=ax.transAxes,
        )
    ax.set_title("End reason counts")
    ax.set_ylabel("Sessions")

    ax = axes[1, 1]
    if {"nearest_drone_start_distance_cells", "time_to_find_s"}.issubset(
        plot_df.columns
    ):
        clean = plot_df[
            ["run", "nearest_drone_start_distance_cells", "time_to_find_s"]
        ].dropna()
        if len(clean):
            sns.scatterplot(
                data=clean,
                x="nearest_drone_start_distance_cells",
                y="time_to_find_s",
                ax=ax,
                s=70,
            )
            if len(clean) >= 3:
                sns.regplot(
                    data=clean,
                    x="nearest_drone_start_distance_cells",
                    y="time_to_find_s",
                    ax=ax,
                    scatter=False,
                    color="gray",
                )
            if len(clean) <= 20:
                for _, row in clean.iterrows():
                    ax.annotate(
                        f"run {int(row['run'])}",
                        (
                            row["nearest_drone_start_distance_cells"],
                            row["time_to_find_s"],
                        ),
                        xytext=(5, 5),
                        textcoords="offset points",
                    )
        ax.set_xlabel("Nearest drone start distance (cells)")
        ax.set_ylabel("Time to find person (s)")
    else:
        ax.text(
            0.5,
            0.5,
            "Distance columns missing",
            ha="center",
            va="center",
            transform=ax.transAxes,
        )
    ax.set_title("Initial distance vs search time")

    plt.tight_layout(rect=[0, 0, 1, 0.95])
    plt.savefig(out / "dashboard.png", dpi=180)
    plt.close()


def save_plots(df: pd.DataFrame, out: Path) -> None:
    out.mkdir(parents=True, exist_ok=True)
    details = out / "details"
    details.mkdir(parents=True, exist_ok=True)
    sns.set_theme(style="whitegrid")

    def plot_box(x: str, y: str, filename: str) -> None:
        if x in df and y in df and df[y].notna().any():
            plt.figure(figsize=(8, 5))
            sns.boxplot(data=df, x=x, y=y)
            sns.stripplot(data=df, x=x, y=y, color="black", alpha=0.35)
            plt.tight_layout()
            plt.savefig(details / filename, dpi=160)
            plt.close()

    plot_box("drone_count", "time_to_find_s", "time_to_find_by_drone_count.png")
    plot_box(
        "communication_radius",
        "swarm_propagation_delay_s",
        "swarm_delay_by_communication_radius.png",
    )
    plot_box(
        "planner_type", "time_to_mission_complete_s", "mission_time_by_planner_type.png"
    )

    if {"nearest_drone_start_distance_cells", "time_to_find_s"}.issubset(df.columns):
        plt.figure(figsize=(7, 5))
        sns.regplot(data=df, x="nearest_drone_start_distance_cells", y="time_to_find_s")
        plt.tight_layout()
        plt.savefig(details / "distance_vs_time_to_find.png", dpi=160)
        plt.close()

    if "planner_type" in df:
        rate = (
            df.groupby("planner_type")["mission_complete"]
            .mean()
            .reset_index(name="success_rate")
        )
        plt.figure(figsize=(7, 5))
        sns.barplot(data=rate, x="planner_type", y="success_rate")
        plt.ylim(0, 1)
        plt.tight_layout()
        plt.savefig(details / "success_rate_by_planner_type.png", dpi=160)
        plt.close()

    if {"communication_radius", "time_to_mission_complete_s"}.issubset(df.columns):
        med = (
            df.groupby("communication_radius", dropna=False)[
                "time_to_mission_complete_s"
            ]
            .median()
            .reset_index()
        )
        plt.figure(figsize=(7, 5))
        sns.lineplot(
            data=med,
            x="communication_radius",
            y="time_to_mission_complete_s",
            marker="o",
        )
        plt.tight_layout()
        plt.savefig(
            details / "median_mission_time_by_communication_radius.png", dpi=160
        )
        plt.close()


def main() -> None:
    parser = argparse.ArgumentParser(description="Analyze DroneTelemetry CSVs.")
    parser.add_argument("--input", type=Path, default=Path("data") / "telemetry")
    parser.add_argument("--out", type=Path, default=Path("analysis") / "output")
    parser.add_argument(
        "--include-destroyed",
        action="store_true",
        help="Do not filter interrupted destroyed rows",
    )
    args = parser.parse_args()

    df = load_summary(args.input, args.include_destroyed)
    write_tables(df, args.out / "tables")
    save_dashboard(df, args.out / "plots")
    save_plots(df, args.out / "plots")
    print(f"Analyzed {len(df)} sessions")
    print(f"tables: {args.out / 'tables'}")
    print(f"plots:  {args.out / 'plots'}")


if __name__ == "__main__":
    main()
