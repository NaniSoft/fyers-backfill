import os
import sqlite3
import pandas as pd
import pyarrow.parquet as pq

D = r"C:\Users\dpven\fyers-data\by-isin"
DB = r"C:\Users\dpven\fyers-data\_ledger.db"
c = sqlite3.connect(DB)
cur = c.cursor()

CASES = [("INE0ALR01029", "NSE:ATALREAL-EQ", "2020-12-01", "2023-11-16"),
         ("INE555Z01020", "NSE:TARACHAND-EQ", "2021-01-13", "2024-12-05"),
         ("INE0A0S01028", "NSE:SIGMA-EQ", "2021-08-23", "2025-10-06"),
         ("INE807F01027", "NSE:VIYASH-EQ", None, None),
         ("INE807F01027", "NSE:SEQUENT-EQ", None, None)]

for isin, sym, gap_from, era_from in CASES:
    print(f"\n=== {isin} {sym}")
    cdir = os.path.join(D, isin, "cash")
    files = sorted(os.listdir(cdir)) if os.path.isdir(cdir) else []
    for f in files:
        t = pq.read_table(os.path.join(cdir, f), columns=["ist_minute"]).to_pandas()
        days = sorted({x[:10] for x in t.ist_minute})
        print(f"   file {f:26} rows={len(t):>8}  {days[0]} .. {days[-1]}  ({len(days)} days)")
    cur.execute("SELECT from_date, to_date, status, rows FROM progress WHERE symbol=? ORDER BY from_date",
                (sym,))
    led = cur.fetchall()
    print(f"   ledger units for {sym}: {len(led)}")
    for fd, td, st, r in led:
        print(f"      {fd}..{td}  {st:8} rows={r:,}")
    if gap_from:
        pre = [x for x in led if x[0] < era_from]
        print(f"   windows requested BEFORE the era start ({era_from}): {len(pre)}"
              f"  -> total rows {sum(x[3] for x in pre):,}")

c.close()
