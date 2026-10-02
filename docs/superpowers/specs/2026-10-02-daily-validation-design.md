# Daily pull-and-validate pipeline — design

*Date: 2026-10-02 · Status: approved*

`data/` is the source of truth we ship (the ISIN-organised 1-minute dataset);
`eod2_data/` is the independent NSE-bhavcopy reference the eod2 app refreshes
daily. This design makes their comparison a **repeatable daily job** inside
`Fyers.Backfill`, replacing the one-off September audit with a command that
pulls the newest day, re-validates, and reports only what is new.

## Inputs

| path | role |
|---|---|
| `data/<ISIN>/cash/<SYMBOL>-EQ.parquet` | ours — 1-minute bars, Parquet/Zstd, written by pyarrow during compaction (schema = `CandleRowDto`) |
| `data/_manifest.csv` | isin, kind, symbol, path — inventory of every cash file |
| `eod2_data/daily/<stem>.csv` | reference — `Date,Open,High,Low,Close,Volume,Series,…` per symbol |
| `eod2_data/isin.csv` | ISIN ↔ SYMBOL ↔ SERIES mapping |
| `eod2_data/meta.json` | reference freshness (its `lastUpdate` is quoted in our report) |

Gitignored: `data/` and `eod2_data/` never enter version control.

## Commands

### `validate` (offline — no token)

```
fyers-backfill validate [--dataset data] [--eod2 eod2_data]
                        [--from 2017-07-03] [--accept-baseline]
```

1. Aggregates ours to daily bars (§Aggregation), incrementally via cache.
2. Loads the reference and joins by ISIN (§Join).
3. Applies the tolerances (§Tolerances), filters through the known-issues
   baseline (§Baseline).
4. Writes `data/_validation/report-YYYY-MM-DD.md`,
   `anomalies-YYYY-MM-DD.csv`, `isin_coverage-YYYY-MM-DD.csv`.
5. Exit codes: **0** clean or baseline-only · **1** new anomalies ·
   **2** config/path errors.

`--accept-baseline` writes every anomaly found in this run into
`data/_validation/known_issues.csv` instead of failing — the bootstrap for the
first run, so the 27,713 permanently-missing days from the September audit are
absorbed once, not re-reported daily.

### `daily` (the one-command morning run)

```
fyers-backfill daily
```

Chains, each stage idempotent and resumable:

1. **Token check** — stale/missing token parks the run with a clear message
   and exit 2 (`login` is the manual one-click step; see §Credentials).
2. **`update`** — existing `BackfillRunner.RunMode.Update` pulls the newest
   window per instrument into `data/backfill/1min/` (the configured
   `backfill.root`).
3. **`organize`** (new) — folds each fresh `1min/<SYMBOL>-EQ.parquet` into
   `data/<ISIN>/cash/<SYMBOL>-EQ.parquet`, merge-on-write keyed by `ts_utc`
   (same semantics as `MergeWriteAsync` / `compact_parts2.py`). ISIN resolved
   from `_manifest.csv`, falling back to `eod2_data/isin_symbol_map.json` for
   former tickers. Files it cannot map are left in `1min/` and reported.
4. **`validate`** — as above.

A run interrupted by the daily budget, auth expiry, or a crash resumes where
it left off; earlier stages stay done.

## Aggregation (ours: minute → daily)

Per ISIN parquet file, streaming:

- de-duplicate by `ts_utc` (keep first);
- IST day key = `(ts_utc + 19800) / 86400` — matches `build_our_eod.py`;
- per day: `open` = earliest minute's open, `close` = latest minute's close,
  `high` = max, `low` = min, `volume` = Σ, plus `bars` count and
  first/last `ist_minute`.

Validation window starts at the floor **2017-07-03** (Fyers' documented
1-minute floor). Reference days before the floor are never counted missing.

### Incremental cache

`data/_validation/cache/<ISIN>.parquet` — that ISIN's daily rows.
`data/_validation/state.json` — per ISIN, the source fingerprint
(length + last-write UTC). A run re-aggregates only ISINs whose fingerprint
changed; the first run is the full ~13 GB sweep and later runs touch only
newly-organized files. A killed sweep finishes on the next run.

## Join

Primary key on both sides is **ISIN**. Reference stems map through
`isin.csv` (stem = lowercase `SYMBOL`; when an ISIN has several series rows,
`EQ` wins). Four coverage buckets, all always reported:

| bucket | meaning | failure? |
|---|---|---|
| `both` | ISIN present on both sides — the comparable set | per-day checks apply |
| `eod2_only` | in the reference, absent from `data/` | yes, when the reference shows EQ-series volume ≥ floor date |
| `ours_only` | in `data/`, absent from the reference | informational |
| `ref_unmapped` | reference stem maps to no ISIN we know | excluded (the audit's wrong-mapping class, e.g. `hdfcbank.csv`) |

Any `both` pair whose **median close ratio over common days** falls outside
0.8–1.25 is tagged `split_factor` (corporate action applied to one side only):
reported, but excluded from OHLC failure counting so a split never drowns real
defects.

## Tolerances (audit-based)

For each common (ISIN, date), ours vs reference:

| check | rule | rationale |
|---|---|---|
| open / high / low | equal after rounding to the 0.01 tick | the audit found exact agreement |
| close | within **0.5%** either way | last-minute close vs official closing price — definitional |
| volume | within **1%** either way | feed runs ~0.2% under the official total |
| missing day | reference row with volume > 0, none of ours, date ≥ floor | a bhavcopy row exists even for zero-trade days; volume > 0 makes it a real session |

Anything else is an anomaly of kind `ohlc_open` / `ohlc_high` / `ohlc_low` /
`close` / `volume` / `missing_day` / `extra_day` (ours has the day, reference
does not — informational, e.g. Muhurat).

## Known-issues baseline

`data/_validation/known_issues.csv` — `kind,isin,date,note` (date empty for
whole-ISIN kinds such as `split_factor`).

- `--accept-baseline` populates it from the current run.
- Later runs subtract baseline rows before reporting; baseline items appear
  only as counts in the report header.
- The file is hand-editable: delete a row to start flagging it again.

## Reports

Written under `data/_validation/`:

| file | contents |
|---|---|
| `report-YYYY-MM-DD.md` | verdict line; reference `lastUpdate`; coverage buckets; new-anomaly counts by kind; worst offenders; baseline counts for context |
| `anomalies-YYYY-MM-DD.csv` | only new/changed anomalies, both values and the tolerance applied |
| `isin_coverage-YYYY-MM-DD.csv` | every ISIN with its bucket and day counts |
| `known_issues.csv` | the baseline (created by `--accept-baseline`) |
| `cache/`, `state.json` | the incremental aggregation cache |

## Credentials

The `daily` chain needs `FYERS_APP_ID` / `FYERS_SECRET_ID` in `.env`
(gitignored) so `login` can serve the OAuth callback on
`http://127.0.0.1:8001/callback`. One interactive click per day — Fyers resets
tokens ~06:00 IST; `TokenFile` already treats a token as fresh until the next
06:00 IST boundary.

## Testing

xunit, in `Fyers.Backfill.Tests`, mirroring the existing style:

- aggregation: IST day boundary across midnight UTC, `ts_utc` de-dup,
  first/last-minute open/close, high/low max/min, volume sum, bar counts;
- tolerances: exact-tick boundary, exactly 0.5% close, exactly 1% volume,
  missing-day requires reference volume > 0, floor date;
- join: EQ-series preference, `ref_unmapped`, `split_factor` detection at the
  0.8/1.25 edges, all four buckets;
- baseline: subtract, `--accept-baseline` output shape, hand-edit effects;
- exit codes: 0 / 1 / 2 paths;
- incremental state: fingerprint change triggers re-aggregation, unchanged
  reads cache.

**Live prerequisite, done first in implementation:** prove Parquet.Net 6.1
reads a real pyarrow-rewritten file from `data/` end-to-end (they were
rewritten by `compact_parts2.py`, not by this codebase) before anything builds
on it.

## Out of scope

- F&O validation (cash only — the reference has no derivative bhavcopy).
- Repairing data (the validator reports; `repair_candles.py` /
  `CandleSanitizer` remain the repair paths).
- Scheduling (a Task Scheduler entry can call `daily` later; not part of this
  work).
