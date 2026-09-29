"""Re-classify the missing days WITHOUT trusting isin2hist era dates.

The era map records when a name was *registered*, not when the stock started
trading, so a day falling outside every era is not evidence of a missing ticker.
ATALREAL's era says 2023-11-16, yet the ledger shows 24 pre-era windows with
53,344 rows and a file starting 2020-10-29.

A bhavcopy row exists for every trading day even when the security did not trade,
so the real test is the reference's own volume/OHLC on the day.
"""
import os
from collections import Counter

import pandas as pd

O = r"C:\Users\dpven\fyers-data\_verify"
REF = r"C:\Users\dpven\Downloads\BhavDesk-main\eod2\src\eod2_data\daily"

causes = pd.read_csv(os.path.join(O, "missing_day_causes.csv"))
causes["date"] = causes["date"].astype(str).str.slice(0, 10)

need = {(r.isin, r.date): (r.cause, r.era_symbol, r.in_our_files)
        for r in causes.itertuples()}
print("missing day rows:", len(need))

# one pass over the reference files we can actually map
sm = pd.read_csv(os.path.join(O, "eod_isin_summary.csv"))
stem_of = dict(zip(sm["isin"], sm["ref_stems"].fillna("")))

cls = Counter()
real_missing = []
per_cause = Counter()
for isin, date in need:
    raw = str(stem_of.get(isin, ""))
    stem = raw.split(";")[0].strip() if raw else ""
    p = os.path.join(REF, stem + ".csv") if stem else ""
    tag = None
    if p and os.path.exists(p):
        try:
            d = pd.read_csv(p, usecols=["Date", "Open", "High", "Low", "Close", "Volume"])
        except Exception:
            d = None
        if d is not None:
            row = d[d["Date"].astype(str).str.slice(0, 10) == date]
            if row.empty:
                tag = "reference_has_no_row"
            else:
                v = pd.to_numeric(row["Volume"].iloc[0], errors="coerce")
                c = pd.to_numeric(row["Close"].iloc[0], errors="coerce")
                if pd.isna(v) or v == 0:
                    tag = "reference_zero_volume"
                elif pd.isna(c) or c == 0:
                    tag = "reference_zero_close"
                else:
                    tag = "REAL_missing_day"
    if tag is None:
        tag = "no_reference_file"
    cls[tag] += 1
    per_cause[(need[(isin, date)][0], tag)] += 1
    if tag == "REAL_missing_day":
        real_missing.append({"isin": isin, "date": date,
                             "cause": need[(isin, date)][0],
                             "era_symbol": need[(isin, date)][1],
                             "our_files": need[(isin, date)][2],
                             "ref_stem": stem})

print("\nclassification of the", len(need), "missing days:")
for k, v in cls.most_common():
    print(f"  {k:24} {v:>7,}")

print("\nby original cause:")
for (c, t), v in sorted(per_cause.items(), key=lambda x: -x[1]):
    print(f"  {c:26} -> {t:22} {v:>7,}")

if real_missing:
    pd.DataFrame(real_missing).to_csv(os.path.join(O, "real_missing_days.csv"), index=False)
    rm = pd.DataFrame(real_missing)
    print(f"\nREAL missing days written to _verify/real_missing_days.csv: {len(rm):,}")
    print("  distinct ISINs:", rm.isin.nunique())
    print(rm.groupby("cause").size().to_string())
    print("\n  sample:")
    print(rm.head(12).to_string(index=False))
else:
    print("\nNo genuinely missing trading days.")
