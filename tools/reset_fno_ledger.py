"""Reset the ledger rows for the F&O data that was lost in the by-isin --clean rebuild.

Those units are still marked 'done', so the runner would skip them and the data would
never come back. Equity (-EQ/-BE) rows are untouched.
"""
import os
import shutil
import sqlite3
import time

DB = r"C:\Users\dpven\fyers-data\_ledger.db"
bak = DB + ".bak-" + time.strftime("%Y%m%d-%H%M%S")
shutil.copy2(DB, bak)
print("backup:", bak)

c = sqlite3.connect(DB)
cur = c.cursor()
cond = "symbol NOT LIKE '%-EQ' AND symbol NOT LIKE '%-BE'"
cur.execute(f"SELECT status, COUNT(*), COALESCE(SUM(rows),0) FROM progress WHERE {cond} GROUP BY status")
for st, n, r in cur.fetchall():
    print(f"  before: {st:8} units={n} rows={r}")
cur.execute(f"DELETE FROM progress WHERE {cond}")
print("deleted:", cur.rowcount)
cur.execute("SELECT status, COUNT(*), COALESCE(SUM(rows),0) FROM progress GROUP BY status")
for st, n, r in cur.fetchall():
    print(f"  after : {st:8} units={n} rows={r}")
c.commit()
c.close()
