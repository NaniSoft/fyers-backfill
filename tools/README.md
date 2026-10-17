# Data-pipeline tooling

Python and PowerShell helpers that sit *around* `Fyers.Backfill`: the ingest code
writes `1min/` + `_parts/`, and these turn that into the validated, ISIN-organised
dataset. They are operational scripts, not part of the .NET solution — they run
under the venv at `%USERPROFILE%\source\repos\trading\.venv` (pyarrow + pandas).

Paths at the top of each script point at the dataset root
(`C:\Users\dpven\fyers-data`) and the BhavDesk reference
(`C:\Users\dpven\Downloads\BhavDesk-main\eod2\src\eod2_data`); adjust for other
machines.

## Aggregation and layout

| script | what it does |
|---|---|
| `build_our_eod.py` | Aggregates every `by-isin/<ISIN>/cash/*.parquet` into one daily bar per IST trading day into `_verify/our_eod.parquet`. De-duplicates by `ts_utc`, day boundary is `+19800s`. Caching this makes every later check read 81 MB instead of 8 GB. |
| `compact_parts2.py` | Folds `_parts/<res>/<SYMBOL>/*.parquet` into the single `<res>/<SYMBOL>.parquet`, sorted and de-duplicated by `ts_utc`. Much faster than the C# `compact` command (which was ~1.1 s/part, i.e. 22 h for the cash set; this does it in minutes). |
| `organize_by_isin.py` | Moves `1min/*.parquet` into `by-isin/<ISIN>/{cash,fno}/`, index F&O into `by-isin/_INDEX/<STEM>/`, and writes `by-isin/_manifest.csv`. `--apply` to act, `--clean` for a full rebuild. Handles `-BE` tickers and expired contracts whose underlying is an index. |
| `verify_by_isin2.py` | September-2026 coverage report. **Footer-only** — it reads row-group statistics, because the naive version pulled 1.3 B `ist_minute` values into Python lists. |
| `isin_index.py` | Rebuilds the shared `isin_symbol_map.json` (ISIN → name eras). |

## Validation

| script | what it does |
|---|---|
| `validate_eod.py` | Compares `our_eod.parquet` against the reference: day-set differences, tick-aware OHLC differences, volume, internal consistency, session completeness. Writes `eod_day_anomalies.csv`, `eod_isin_summary.csv`. |
| `eod_triage.py` | Per-ISIN triage with flags (`sentinel_volume`, `bad_bars`, `basis_mismatch`, …) — the file to start from. |
| `eod_report_final.py` | Renders `_verify/EOD_VALIDATION_REPORT.md` from the artefacts above. |
| `repair_candles.py` | Applies the `CandleSanitizer` rules to existing parquet in place: drop non-positive prices, clamp high/low, zero sentinel volumes. `python repair_candles.py [VOL_MAX] [only_these.txt]`. |
| `session_collate.py` | Classifies every trading date and collates the Muhurat sessions. |
| `out_of_session.py` | Bars outside 09:15–15:30 IST. **Compares times numerically** — `HH:MM` strings compare wrongly (`"9:44" > "15:29"`), which inflated an earlier count from 14,526 to 24,270. |
| `name_era_gap.py` | Which historical name eras we hold data for. |
| `missing_day_causes.py` | Attributes each missing day to a cause. |
| `reclassify_missing.py` | Re-checks "missing" days against the reference's own volume — a bhavcopy row exists even for a day the security did not trade. |
| `era_outcome.py` | Per name era: requested, served, or retired; and whether another ticker already covered it. |
| `gap_shape.py` | Collapses missing days into contiguous blocks. |
| `muhurat_check.py`, `muhurat_vs_ref.py` | Muhurat coverage, and its closes cross-checked against the reference. |
| `ledger_proof.py` | Shows exactly which windows were requested per symbol and how many rows came back — the evidence for "Fyers does not have it". |

## Ledger repair

| script | what it does |
|---|---|
| `reset_fno_ledger.py` | Deletes non-equity ledger rows so deleted F&O files get re-pulled instead of being skipped. Backs the ledger up first. |
| `seed_dead_tickers.py` | Seeds the `dead_symbols` meta key from a run log (EQ *and* BE both rejected). |

## Runners

`run-cash-full.ps1`, `run-historical.ps1`, `run-fno-sep.ps1`, `run-repair.ps1`,
`run-our-eod.ps1` start a step detached with a log. `finish_pipeline4.ps1` chains
compact → organize → verify. `resume-all.ps1` waits for a fresh Fyers token and then
runs the remaining passes plus the whole offline pipeline.

**Start long runs through `Win32_Process.Create`, not `Start-Process`.** A plain
child process is killed along with the agent shell's process tree when the server
restarts — that silently killed two F&O runs mid-flight with an empty stderr and no
crash event in the Application log. `supervise-fno.ps1` shows the pattern.
