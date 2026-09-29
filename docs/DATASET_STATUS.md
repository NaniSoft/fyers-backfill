# fyers-backfill — dataset status

_Last updated 2026-09-29 09:10 IST. Generated from `_ledger.db` and `_verify/`._

## Where the data lives

```
C:\Users\dpven\fyers-data\
  by-isin\<ISIN>\cash\*.parquet     one file per ticker, full history, 1-minute
  by-isin\<ISIN>\fno\*.parquet      F&O on that underlying's equity
  by-isin\_INDEX\<NIFTY|BANKNIFTY|FINNIFTY|MIDCPNIFTY>\   index futures & options
  by-isin\_manifest.csv             inventory of every file with its ISIN
  _verify\                          EOD validation report + all anomaly CSVs
  _masters\                         cached Fyers symbol masters
  _ledger.db                        what has been fetched (the resume ledger)
```

## What is complete

| | |
|---|---|
| **Cash equities** | **2,919 ISINs · 1,296,365,708 rows · 2017-07-03 → 2026-09-24** |
| — September-2026 coverage | 2,871 ISINs complete on all 17 trading days, 48 incomplete (all explained) |
| **F&O (Sep 2026)** | 53,982 underlying files + 7,588 index files = **61,570 of 81,821 contracts (75%)** |
| — with trades | 20,893 files, 48,028,895 rows (33,089 contracts returned no trades) |
| Dataset size | **12.8 GB** in `by-isin`, 75 GB free on C: |
| Ledger | 152,613 units done, 650 failed |

Cash is **finished** — 2017 to date, every listed equity, repaired and validated.

## What remains

1. **September-2026 F&O: 27,992 contracts still to pull.** Blocked only by the access token.
2. **Expired-futures full history** — a separate pass (`fb-futures-full.yaml`), ~44k requests.
3. **Expired options: impossible.** Fyers returns `no_data` for every expired option
   contract (live-verified); no source can supply them.

## How to resume

1. Log in (interactive, needs port 8001 free — **stop Docker Desktop first**):
   ```
   C:\Users\dpven\AppData\Local\Temp\opencode\fb-login-interactive.py
   ```
   then copy `trading\data\fyers_access_token.json` to
   `fyers-backfill\data\fyers_access_token.json`.
2. Start the supervisor — it waits for a fresh token, then runs both remaining
   passes and the whole offline pipeline automatically:
   ```
   powershell -File C:\Users\dpven\fyers-data\_tools\resume-all.ps1
   ```
   It is **already running** (pid in `_tools\resume.log`), so step 1 is all that is needed.
3. The token dies at 06:00 IST. Each day needs one login; the ledger makes every
   restart resumable, and a per-run `daily_budget` keeps the account under Fyers'
   100k/day cap.

## Validation

`EOD_VALIDATION_REPORT.md` (in `_verify\`) is the full write-up: every ISIN checked
against an independent EOD reference, with `eod_isin_triage.csv` as the per-ISIN
starting point. Headline: **open/high/low match the reference exactly**; the close
differs by a few ticks on ~half of days (last 1-minute bar's close vs the official
NSE closing price); volume runs ~0.2% under the official total. Four defect classes
were found and three were repaired in place (1,960 files rewritten, 302k bars
clamped, 8.8k sentinel volumes zeroed).

### Name changes

Fyers serves **most** renames itself — it returns the whole history under the current
ticker, so the rename is invisible (`VIYASH` covers 2017→2026 including the entire
`SEQUENT` era; `ATALREAL` starts 2019-11 even though the ISIN map dates its name from
2023-11-16). For the rest, `universe.historical_equities` pulls every former ticker
from the ISIN map, bounded to its own era: of 981 name eras, **377 pulled OK (134 of
which added coverage nothing else had)**, 423 are gone from Fyers entirely, 179 lie
before 2017-07-03 or were already covered.

**Do not read `isin2hist.from_date` as a listing date.** It records when a *name* was
registered. An earlier pass did exactly that and wrongly reported "210 ISINs with an
unknown pre-rename ticker"; the ledger proves otherwise — ATALREAL's 24 pre-era
windows returned 53,344 rows.

### Genuinely missing

**27,713 days on 1,482 ISINs** have volume in the reference and no bars on our side
(`real_missing_days.csv`, collapsed into 3,429 contiguous blocks in
`real_missing_blocks.csv`). Median block length is 1 day; only 2 blocks exceed 400.
The largest is `PATANJALI` missing `RUCHI`'s 609-day era, because `NSE:RUCHI-EQ` is
retired and Fyers did not stitch it forward. Not recoverable from Fyers.

Also unrepairable: the Muhurat Trading sessions (legitimate — collated in
`muhurat_sessions.csv`), two Muhurat dates Fyers does not serve at all
(2017-10-19, 2024-01-22), and 2021-02-24 where Fyers returns exactly twice the
traded volume.

## Environment gotchas

* A stray **`copy.py`** in `C:\Users\dpven\AppData\Local\Temp\opencode\` (created
  2026-09-29 02:32 by another session) shadows the stdlib `copy` module. Any Python
  script run *from that directory* fails with
  `AttributeError: module 'copy' has no attribute 'deepcopy'`. Run project scripts
  from `fyers-data\_tools\` instead — that is where the pipeline scripts live.
* Long backfill runs must be started through `Win32_Process.Create`, not
  `Start-Process`: a plain child gets killed when the agent server restarts, which
  silently killed two F&O runs mid-flight (empty stderr, no crash event).
* Docker Desktop is stopped so port 8001 is free for the Fyers login. Kubernetes is
  still disabled, so the `fyers-collector` pod is not running.
