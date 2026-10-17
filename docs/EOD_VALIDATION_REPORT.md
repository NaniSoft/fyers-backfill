# Cash EOD validation — fyers-backfill vs BhavDesk eod2

`_verify/our_eod.parquet` — 4,042,123 daily bars aggregated from the 1-min parquet — compared against 3,571 reference symbols (8,100,867 daily rows) in `C:\Users\dpven\Downloads\BhavDesk-main\eod2\src\eod2_data\daily`.

* our ISINs **2,914** · compared **2,914** · reference ISINs we do not carry **657** · our ISINs with no reference **0**
* window **2017-07-03 → 2026-09-24** · ISINs with no anomaly at all **10** of 2,914
* 0 non-monotonic files, 0 duplicate minutes, 0 unmapped ISINs

## Verdict

The dataset is **usable and now internally sound**. Four defect classes were found; three were repaired in place, one is not repairable and is enumerated below.

| | before | after |
|---|---|---|
| days with a non-positive price | 954 | **0** |
| days whose high is below its open/close | 6,038 | **0** |
| days whose low is above its open/close | 4,315 | **0** |

| sentinel volumes zeroed (per-minute bars) | 3,234 affected days | **0** |

(A *daily* total above 1e8 shares is normal for a liquid name — 8,264 days legitimately exceed it. The guard is per-minute, and after the repair no minute bar carries a sentinel: the worst day is now checked against the reference below.)

Remaining differences are definitional or reference-side, not defects — see A and C.

## A. Definitional differences (no action needed)

### A1. Open / high / low match exactly; the close often does not

```
MAYURUNIQ   2026-09-24  O/H/L 722.90 / 738.15 / 718.00  == reference exactly
                                    close  ours 732.00   ref 733.10
CARERATING 2026-09-24  O/H/L 1638.3 / 1638.3 / 1610.0  == reference exactly
                                    close  ours 1615.00  ref 1616.80
KRBL       2026-09-24  O/H/L 404.75 / 410.95 / 396.20  == reference exactly
                                    close  ours 398.70  ref 398.20
```

The close moves in **both** directions and is not a constant offset: it is the last one-minute bar's close (what we store) against the official NSE closing price (what the reference stores). They agree on days with no closing-auction print. Mega-caps agree almost perfectly (RELIANCE 38/38 days, TCS 37/38 in Sep 2026).

### A2. Volume runs ~0.2% under the official total

`ours / reference` = median **0.9992**, 5th percentile 0.9957. Always slightly under.

## B. Defects

### B1-B3. Repaired

| defect | root cause | repair |
|---|---|---|
| impossible bars, e.g. `JISLDVREQS 2017-11-10 09:15` = `open 73.00, high 72.00` | Fyers serves a bar whose high is below its open | `high = max(o,h,l,c)`, `low = min(o,h,l,c)` |
| volume sentinels, e.g. `429496726000` = `2^32·100 − 3600`; GOLDBEES hit 2.1e12 in a day | a negative volume arrives wrapped and multiplied | `volume < 0 or > 1e8` → 0 |
| non-positive prices | same bad-tick family | the bar is dropped |

Applied to all 2,688 files: 1,960 rewritten, 9,113 bars dropped, 302,230 clamped, 8,770 sentinel volume values zeroed. The same rules now run at ingest (`Fyers.Core.Fyers.CandleSanitizer`) for both `/data/history` and the expired F&O endpoint, so future pulls are clean by construction.

Spot check of the four worst days, against the reference:

| symbol | date | before | after | reference |
|---|---|---|---|---|
| LUMAXTECH | 2017-10-23 | 6,838,735,555 | 5,401 | 27,302 |
| YESBANK | 2017-07-10 | 5,154,023,053 | 75,488 | 315,651 |
| UNITDSPR | 2017-07-10 | 4,294,983,629 | 20,014 | 83,437 |
| CANFINHOME | 2017-07-10 | 4,294,972,759 | 6,025 | 27,690 |

The volume threshold is empirical, not a guess: across 32,001,283 positive minute volumes from the 40 most liquid names the distribution tops out at p99.9 = 6.6M, p99.99 = 19.1M and p99.999 = 50.8M shares, while the corrupt values start at 8.5e8.

### B4. Irregular sessions — legitimate, and now collated

The 58–60 bar days at 18:00–19:14 are the NSE **Muhurat Trading** session (the one-hour Diwali evening session), not corrupt data. They cross-validate against the reference: median close difference 0.19–0.33%, the same order as an ordinary day.

| date | weekday | symbols we hold | median bars | window served | note |
|---|---|---|---|---|---|
| 2017-10-19 | Thursday | 3 | 60 | 18:30-19:29 | Muhurat (Diwali) - NOT available from Fyers (3 symbols only) |
| 2018-11-07 | Wednesday | 1,302 | 59 | 17:30-18:29 | Muhurat (Diwali) |
| 2019-10-27 | Sunday | 1,339 | 58 | 18:15-19:14 | Muhurat (Diwali) |
| 2020-11-14 | Saturday | 1,394 | 60 | 18:15-19:14 | Muhurat (Diwali) |
| 2021-11-04 | Thursday | 1,459 | 60 | 18:15-19:14 | Muhurat (Diwali) |
| 2022-10-24 | Monday | 1,613 | 60 | 18:15-19:14 | Muhurat (Diwali) |
| 2023-11-12 | Sunday | 1,803 | 60 | 18:15-19:14 | Muhurat (Diwali) |
| 2024-11-01 | Friday | 2,009 | 60 | 18:00-18:59 | Muhurat (Diwali) |
| 2025-10-21 | Tuesday | 2,235 | 61 | 13:45-14:45 | Muhurat (Diwali) - Fyers timestamps it 13:45-14:45 IST |

Two Muhurat dates are **not available from Fyers at all** — probed with a single-day request through the real client, which returns 0 bars:

* `2017-10-19` (Diwali Muhurat) — we hold 3 symbols; the reference has the day for many more, so it is recoverable as an EOD bar but not as 1-minute data.
* `2024-01-22` (the special one-hour session) — no data anywhere we can reach.

Fyers also mislabels some of these timestamps: 2019-2023 come back 15 minutes late (18:15–19:14 for an 18:00–19:00 session) and **2025-10-21 comes back at 13:45–14:45 IST**, mid-session. The prices are right; only the clock is wrong.

Beyond Muhurat, `session_calendar.csv` classifies every trading date. The other irregular cases:

| what | symbol-days | assessment |
|---|---|---|
| 09:00–09:08 pre-open session included | 3,282 | legitimate (NSE pre-open) |
| bars ending after 15:30, not a Muhurat date | 1,535 | see below |
| 2021-02-24, whole universe served 09:15–17:00 | 1,318 | **volume 1.997× the reference** |

`2021-02-24` is the one genuine source defect left: Fyers returns a mangled timestamp sequence (09:15–11:29, 15:00–15:29, 16:00–17:00) and exactly twice the traded volume. Prices still match the reference to a tick (O/H/L 1015.00/1032.63/1013.08/1030.28 vs 1015.00/1032.60/1013.10/1030.50) — only the volume is wrong, and a fresh single-day request returns the identical 225 bars, so it cannot be re-pulled. Treat that one date's volume as unusable.

## B5. Days the reference has and we do not

27,713 days on 1,482 ISINs. Every one of them has non-zero volume in the reference, so none is a non-trading day. They fall into 3,429 contiguous blocks, median length **1 day**:

| block length | blocks |
|---|---|
| 1-5 days | 2,758 |
| 6-30 days | 461 |
| 31-120 days | 180 |
| 121-400 days | 28 |
| >400 days | 2 |

### How name changes are handled

**Fyers serves most renames itself.** It returns the whole history under the *current* ticker, so the rename is invisible in the data:

```
INE807F01027  VIYASH   2017-07-03 .. 2026-09-24  (2,287 days)  <- covers the SEQUENT era
INE0ALR01029  ATALREAL 2019-11-13 .. 2026-09-24  (1,044 days)  <- era map claims 2023-11-16
```

**Our backfill covers the rest.** `universe.historical_equities` adds every former ticker from the ISIN map, bounded to its own era. Across the 981 name eras on ISINs with a recorded rename:

| outcome | eras |
|---|---|
| retired not served | 423 |
| pulled ok | 377 |
| never requested | 179 |
| pulled empty | 2 |

Of the 377 we pulled successfully, **134 added coverage nothing else had** and 243 turned out to be eras Fyers had already stitched onto another ticker we already had.

**423 former tickers are simply gone from Fyers** (rejected as `Invalid symbol` in both the EQ and BE series, now retired in the ledger). Where Fyers did not stitch the history onto the new name *and* the old ticker is retired, the gap is permanent. The largest is:

| ISIN | our ticker | missing | days | old ticker |
|---|---|---|---|---|
| INE619A01035 | NSE_PATANJALI | 2020-01-27 → 2022-07-12 | 609 | RUCHI |
| INE351Y01019 | NSE_BETA | 2021-08-23 → 2023-05-09 | 413 | ? |
| INE230B01021 | NSE_CREATIVEYE | 2021-11-08 → 2023-04-26 | 335 | ? |
| INE747K01017 | NSE_TARAPUR | 2022-04-11 → 2023-07-07 | 299 | ? |
| INE696K01024 | NSE_PRAKASHSTL | 2019-01-14 → 2020-04-09 | 294 | ? |

`PATANJALI` is the clearest case: `NSE:RUCHI-EQ` is retired, and although the current ticker does reach back to 2019-11-13, Fyers has no data at all for 2020-01-27 → 2022-07-12.

### A correction

An earlier pass reported "210 ISINs whose pre-rename ticker cannot be derived". **That was wrong.** It came from reading `isin2hist`'s `from_date` as the first trading day; the field records when a *name* was registered, not when the security started trading. ATALREAL's era begins 2023-11-16, yet its ledger shows 24 pre-era windows returning 53,344 rows and a file starting 2019-11-13. The genuine gap count is the 27,713 days above, and it is a Fyers coverage limit, not a mapping gap.

We also hold **48,244** days the reference lacks — reference-side gaps.

## C. Reference-side caveats

### C1. Different price basis

57 ISINs sit a **constant** factor away from the reference (a split or bonus applied to one side only), so a bar-for-bar comparison is meaningless there:

| our symbol | our/ref median | implied factor |
|---|---|---|
| NSE_CUPID-EQ | 9.9696 | 0.100 |
| NSE_BHAGCHEM-EQ | 9.8420 | 0.102 |
| NSE_REFEX-EQ | 4.9933 | 0.200 |
| NSE_BBL-EQ | 1.9958 | 0.501 |
| NSE_PERSISTENT-EQ | 1.9957 | 0.501 |
| NSE_CCAVENUE-EQ | 0.8947 | 1.118 |
| NSE_SAMMAANCAP-EQ | 0.8920 | 1.121 |
| NSE_LLOYDSENGG-EQ | 0.8919 | 1.121 |

### C2. The reference's own stem→ISIN mapping is sometimes wrong

`hdfcbank.csv` trades at ~715 with ~39M volume while our `INE090A01021` is ~1,352 with ~7M; `lal.csv` has days 70x off. A price ratio far from 1 is the tell — treat it as suspect mapping, not bad data.

## D. Session completeness

A full NSE cash session is 375 one-minute bars. Our distribution:

| bars | days |
|---|---|
| < 1 | 0 |
| < 10 | 50,189 |
| < 100 | 268,937 |
| < 300 | 930,324 |
| < 370 | 1,592,409 |
| = 375 | 1,913,558 |

99,581 days have fewer than 30 bars and 9,507 have exactly one. For illiquid instruments Fyers emits a bar only when something traded, so this is expected — but a one-bar 'day' is worth knowing about before computing indicators. (Note: a per-date *median* bar count is dominated by these names and is not a useful health metric.)

## Files

| file | contents |
|---|---|
| `our_eod.parquet` | our daily bars (isin, date, O/H/L/C/V, bars, first/last minute) |
| `our_eod_pre_repair.parquet` | the same, before the repair — kept for before/after |
| `eod_isin_triage.csv` | one row per ISIN with every flag and count — **start here** |
| `eod_isin_summary.csv` | per-ISIN day counts, overlap, anomaly counts, worst diffs |
| `eod_day_anomalies.csv` | every anomalous ISIN-day with both values and the tolerance used |
| `out_of_session_days.csv` | symbol-days with bars outside 09:15-15:30 IST (Muhurat + irregular) |
| `session_calendar.csv` | every trading date classified: normal / pre-open / muhurat / thin / after-hours |
| `muhurat_sessions.csv` | all 13,157 symbol-days on the Muhurat dates, with OHLCV and the window served |
| `missing_day_causes.csv` | each missing day attributed to a cause |
| `pullable_historical_eras.csv` | former tickers, with the days each would recover |
| `unknown_pre_rename_tickers.csv` | 210 ISINs whose pre-rename ticker cannot be named |
| `eod_ratio_profile.csv` | per-ISIN price/volume ratio vs the reference |
| `name_era_coverage.csv` | every historical name era and whether we hold data for it |
