"""For every ISIN with a recorded name change: did we request the former ticker,
did Fyers serve it, and does the CURRENT ticker already cover the old era?"""
import json
import os
import sqlite3
from collections import Counter

import pandas as pd
import pyarrow.parquet as pq

D = r"C:\Users\dpven\fyers-data\by-isin"
DB = r"C:\Users\dpven\fyers-data\_ledger.db"
T = r"C:\Users\dpven\source\repos\trading\isin"

hist = json.load(open(os.path.join(T, "isin_symbol_map.json"), encoding="utf-8"))["isin2hist"]
c = sqlite3.connect(DB)
cur = c.cursor()
cur.execute("SELECT v FROM meta WHERE k='dead_symbols'")
row = cur.fetchone()
dead = set(json.loads(row[0])) if row and row[0] else set()
cur.execute("SELECT symbol, COALESCE(SUM(rows),0), COUNT(*) FROM progress "
            "WHERE status='done' GROUP BY symbol")
done = {s: (r, n) for s, r, n in cur.fetchall()}

stats = Counter()
rows_out = []
for isin, eras in hist.items():
    if len(eras) < 2:
        continue
    cdir = os.path.join(D, isin, "cash")
    cur_cover = {}
    if os.path.isdir(cdir):
        for f in os.listdir(cdir):
            if not f.endswith(".parquet"):
                continue
            sym = f[:-8]
            t = pq.read_table(os.path.join(cdir, f), columns=["ist_minute"]).to_pandas()
            days = sorted({x[:10] for x in t["ist_minute"]})
            if days:
                cur_cover[sym] = (days[0], days[-1], len(days))
    for e in eras:
        sym = e["symbol"]
        ticker = f"NSE:{sym}-EQ"
        lo, hi = e["from_date"], e["to_date"]
        # did the CURRENT data already cover this era?
        covered_by = [s for s, (a, b, n) in cur_cover.items() if a <= lo and b >= hi and n > 0]
        partial = [s for s, (a, b, n) in cur_cover.items() if not (a <= lo and b >= hi) and n > 0
                   and a <= hi and b >= lo]
        if ticker in done:
            state = "pulled_ok" if done[ticker][0] > 0 else "pulled_empty"
        elif ticker in dead:
            state = "retired_not_served"
        elif f"{sym}-BE" in dead or ticker.replace("-EQ", "-BE") in dead:
            state = "retired_be"
        else:
            state = "never_requested"
        stats[state] += 1
        rows_out.append({
            "isin": isin, "era_symbol": sym, "from": lo, "to": hi, "state": state,
            "rows_pulled": done.get(ticker, (0, 0))[0],
            "our_files": ";".join(sorted(cur_cover)),
            "era_covered_by_our_file": ";".join(covered_by),
            "era_partially_covered": ";".join(partial),
        })

df = pd.DataFrame(rows_out)
df.to_csv(r"C:\Users\dpven\fyers-data\_verify\name_era_request_outcome.csv", index=False)
print("eras across ISINs with a recorded name change:", len(df))
print(df.state.value_counts().to_string())
print("\nof the pulled_ok eras, how many were still NEEDED (not already covered by another file):")
need = df[(df.state == "pulled_ok")]
print("  pulled_ok:", len(need),
      "| era already fully covered by another of our files:",
      int((need.era_covered_by_our_file != "").sum()),
      "| added unique coverage:", int((need.era_covered_by_our_file == "").sum()))
print("\nretired_not_served sample:")
print(df[df.state == "retired_not_served"].head(8)[["isin", "era_symbol", "from", "to", "our_files"]].to_string(index=False))
c.close()
