# inhachuo2

## Native Algorithm

```txt
Native/
```

Builds a shared C++ library and includes GoogleTest-based native tests.

### Quick start

Enter the Nix development shell:

```bash
nix develop
```

Native shortcuts:

```bash
dn-config        # configure native build, tests off
dn-config-tests  # configure native build, tests on + compile_commands.json
dn-build         # build native library/tests
dn-stage        # copy built native plugin into Assets/Plugins/<target>/
dn-unity-plugin  # build native library/tests, then stage the Unity plugin
dn-test          # run native tests
dn-format        # format C++ sources with clang-format
dn-lint          # lint C++ sources with clang-tidy
dn-check         # configure tests, build, test, then lint
```

Recommended first run:

```bash
dn-check
```

To make the built native library available to Unity:

```bash
dn-unity-plugin
```

This creates Unity plugin folders for all supported desktop targets and copies any libraries produced by the native build.

### Editor setup

For C/C++ editor diagnostics and autocomplete, configure tests once:

```bash
dn-config-tests
```

This generates:

```txt
Native/drone-navigation-native/build/compile_commands.json
```

Point your editor or `clangd` integration at that file if it is not detected automatically.

## Python Telemetry Analysis

The project includes Python scripts for collecting and analyzing Unity drone mission telemetry
available through the Nix development shell.

### Input data

Unity writes telemetry CSV files under `Application.persistentDataPath` in:

```txt
DroneTelemetry/session_summary.csv
DroneTelemetry/session_events.csv
```

`session_summary.csv` contains one row per mission/session and is the main input for statistical analysis. `session_events.csv` contains event-level timeline data.

### Collect telemetry

Copy Unity telemetry CSVs into the repository:

```bash
stat-collect
```

By default this writes to:

```txt
data/telemetry/session_summary.csv
data/telemetry/session_events.csv
```

options:

```bash
stat-collect --source /path/to/DroneTelemetry
stat-collect --snapshot
stat-collect --out data/telemetry
```

### Run analysis

Generate derived metrics, summary tables, and plots:

```bash
stat-analyze
```

Default outputs:

```txt
analysis/output/tables/summary_with_derived_metrics.csv
analysis/output/tables/grouped_metrics.csv
analysis/output/tables/end_reason_counts.csv
analysis/output/tables/distance_vs_find_correlation.csv
analysis/output/plots/dashboard.png
analysis/output/plots/details/*.png
```

Interrupted rows with `end_reason = destroyed` are filtered out by default.

Include them with:

```bash
stat-analyze --include-destroyed
```

Use custom paths if needed:

```bash
stat-analyze --input data/telemetry --out analysis/output
```

### Dashboard only

Regenerate only the dashboard image from an existing derived summary table:

```bash
stat-dashboard
```

This reads:

```txt
analysis/output/tables/summary_with_derived_metrics.csv
```

and writes:

```txt
analysis/output/plots/dashboard.png
```
