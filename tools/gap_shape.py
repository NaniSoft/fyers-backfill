import os
from collections import Counter

import pandas as pd
import pyarrow.parquet as pq

O = r"C:\Users\dpven\fyers-data\_verify"
D = r"C:\Users\dpven\fyers-data\by-isin"

rm = pd.read_csv(os.path.join(O, "real_missing_days.csv"))
print("real missing days:", len(rm), "on", rm["isin"].nunique(), "ISINs")
print(rm.groupby("cause").size().to_string())

# contiguous blocks per ISIN
rm["d"] = pd.to_datetime(rm["date"])
blocks = []
for isin, g in rm.groupby("isin"):
    days = sorted(g["d"])
    start = prev = days[0]
    for x in days[1:]:
        if (x - prev).days <= 5:
            prev = x
        else:
            blocks.append((isin, start, prev))
            start = prev = x
    blocks.append((isin, start, prev))
b = pd.DataFrame(blocks, columns=["isin", "from", "to"])
lens = []
for (isin, a, z) in blocks:
    lens.append(int(((rm["isin"] == isin) & (rm["d"] >= a) & (rm["d"] <= z)).sum()))
b["n_days"] = lens
print("\ncontiguous missing blocks:", len(b), "| median length (days):",
      int(b.n_days.median()), "| longest:", int(b.n_days.max()))
buckets = pd.cut(b.n_days, [0, 5, 30, 120, 400, 100000],
                 labels=["<=5", "6-30", "31-120", "121-400", ">400"])
print("blocks by length:", buckets.value_counts().to_dict())
print("\nlongest blocks:")
b2 = b.sort_values("n_days", ascending=False).head(10).copy()
b2["our_files"] = b2["isin"].map(rm.groupby("isin")["our_files"].first())
print(b2.to_string(index=False))
b.to_csv(os.path.join(O, "real_missing_blocks.csv"), index=False)

# --- the SEQUENT -> VIYASH case
print("\n=== SEQUENT (INE807F01027) -> VIYASH")
cdir = os.path.join(D, "INE807F01027", "cash")
for f in sorted(os.listdir(cdir)):
    t = pq.read_table(os.path.join(cdir, f), columns=["ist_minute"]).to_pandas()
    days = sorted({x[:10] for x in t["ist_minute"]})
    print(f"  {f:26} rows={len(t):>8}  {days[0]} .. {days[-1]}  ({len(days)} days)")
