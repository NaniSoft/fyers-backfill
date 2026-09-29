import os
import pandas as pd
import numpy as np
import pyarrow.parquet as pq

O = r"C:\Users\dpven\fyers-data\_verify"
D = r"C:\Users\dpven\fyers-data\by-isin"
o = pd.read_parquet(O + r"\our_eod.parquet")

# NSE Muhurat Trading (one-hour evening session on Diwali Laxmi Pujan) plus the
# special January 2024 session, and the day before/after each for contrast.
MUHURAT = ["2017-10-19", "2018-11-07", "2019-10-27", "2020-11-14", "2021-11-04",
           "2022-10-24", "2023-11-12", "2024-01-22", "2024-11-01", "2025-10-21"]
NEIGHBOUR = ["2017-10-18", "2018-11-06", "2019-10-25", "2020-11-13", "2021-11-03",
             "2022-10-25", "2023-11-10", "2024-10-31", "2025-10-20"]

print(f"{'date':12} {'dow':10} {'syms':>6} {'medbars':>8} {'min':>6} {'max':>6}  session")
for d in MUHURAT + NEIGHBOUR:
    s = o[o.date == d]
    if s.empty:
        print(f"{d:12} {pd.Timestamp(d).day_name():10} {0:>6} {'-':>8} {'-':>6} {'-':>6}  (no data)")
        continue
    tag = "MUHURAT" if d in MUHURAT else "normal day"
    print(f"{d:12} {pd.Timestamp(d).day_name():10} {len(s):>6} {int(s.bars.median()):>8} "
          f"{s.bars.min():>6} {s.bars.max():>6}  {tag}  "
          f"first={s.first_min.min()} last={s.last_min.max()}")

print("\n=== bar count distribution on each Muhurat date")
for d in MUHURAT:
    s = o[o.date == d]
    if s.empty:
        continue
    vc = s.bars.value_counts().head(4).to_dict()
    print(f"  {d}: {dict(sorted(vc.items()))}  (n={len(s)})")

print("\n=== a liquid name on each Muhurat date")
for isin, sym in (("INE002A01018", "NSE_RELIANCE-EQ"), ("INE062A01020", "NSE_SBIN-EQ")):
    p = os.path.join(D, isin, "cash", sym + ".parquet")
    if not os.path.exists(p):
        continue
    t = pq.read_table(p, columns=["ist_minute"]).to_pandas()
    have = {x[:10] for x in t.ist_minute}
    print(f"  {sym}: " + "  ".join(f"{d}={'Y' if d in have else 'N'}" for d in MUHURAT))
    break
