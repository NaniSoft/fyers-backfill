import os
import numpy as np
import pandas as pd

O = r"C:\Users\dpven\fyers-data\_verify"
REF = r"C:\Users\dpven\Downloads\BhavDesk-main\eod2\src\eod2_data\daily"
MUHURAT = ["2017-10-19", "2018-11-07", "2019-10-27", "2020-11-14", "2021-11-04",
           "2022-10-24", "2023-11-12", "2024-01-22", "2024-11-01", "2025-10-21"]

ours = pd.read_parquet(O + r"\our_eod.parquet")
sm = pd.read_csv(O + r"\eod_isin_summary.csv")

print("=== does the reference carry the Muhurat sessions?")
for d in MUHURAT:
    n_ours = int((ours.date == d).sum())
    # count reference files with a row on that date
    files = [f for f in sm.ref_stems.dropna().astype(str).head(400) if f]
    stems = sorted({s for f in files for s in f.split(";") if s})
    hits = 0
    checked = 0
    for s in stems[:150]:
        p = os.path.join(REF, s + ".csv")
        if not os.path.exists(p):
            continue
        checked += 1
        try:
            r = pd.read_csv(p, usecols=["Date", "Close", "Volume"])
        except Exception:
            continue
        if (r.Date.astype(str).str[:10] == d).any():
            hits += 1
    print(f"  {d}  ours={n_ours:>5} symbols   reference rows found in {hits}/{checked} sampled files")

print("\n=== OHLCV comparison on the Muhurat dates we hold (sample of 300 symbols)")
for d in MUHURAT:
    sub = ours[ours.date == d]
    if len(sub) < 50:
        print(f"  {d}: only {len(sub)} symbols in our data — skipped")
        continue
    exact_close = 0
    checked = 0
    rels = []
    stem_of = dict(zip(sm["isin"], sm["ref_stems"].fillna("")))
    for r in sub.sample(min(300, len(sub)), random_state=1).itertuples():
        raw = str(stem_of.get(r.isin, ""))
        stem = raw.split(";")[0].strip() if raw else None
        if not stem:
            continue
        p = os.path.join(REF, stem + ".csv")
        if not os.path.exists(p):
            continue
        try:
            rr = pd.read_csv(p, usecols=["Date", "Open", "High", "Low", "Close", "Volume"])
        except Exception:
            continue
        m = rr[rr.Date.astype(str).str[:10] == d]
        if m.empty:
            continue
        checked += 1
        x = m.iloc[0]
        if abs(r.close - float(x.Close)) < 0.011:
            exact_close += 1
        rels.append(abs(r.close - float(x.Close)) / max(float(x.Close), 1e-9))
    if checked:
        print(f"  {d}: checked={checked:>4}  exact close match={exact_close:>4} "
              f"({100*exact_close/checked:.1f}%)  median |rel|={np.median(rels)*100:.3f}%")
    else:
        print(f"  {d}: no comparable reference rows")
