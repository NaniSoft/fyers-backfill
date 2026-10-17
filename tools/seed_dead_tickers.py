"""Seed the runner's retired-ticker list from the historical pass log.

Only counts an error where the EQ attempt AND the BE fallback both came back
'Invalid symbol' -- the original Sep-2026 run predates the BE fallback, so its
failures (MILKYMIST, ALKALI, CORDSCABLE ...) are recoverable and must NOT be retired.
"""
import json, re, sqlite3, sys

LOG = r"C:\Users\dpven\fyers-data\_tools\historical.log"
DB = r"C:\Users\dpven\fyers-data\_ledger.db"

pat = re.compile(
    r"ERROR.*backfill:\s+(?P<sym>NSE:[A-Z0-9&._-]+?)-EQ\s+\d\d/\d\d/\d\d\.\."
    r"\d\d/\d\d/\d\d failed: history (?P<be>NSE:[A-Z0-9&._-]+-BE) -> error: Invalid symbol provided")

dead = set()
for line in open(LOG, encoding="utf-8", errors="replace"):
    m = pat.search(line)
    if m:
        dead.add(m.group("sym"))

print("retiring:", len(dead), "tickers")
c = sqlite3.connect(DB)
cur = c.cursor()
cur.execute("SELECT v FROM meta WHERE k = 'dead_symbols'")
row = cur.fetchone()
existing = set(json.loads(row[0])) if row and row[0] else set()
print("already retired:", len(existing))
merged = sorted(dead | existing)
cur.execute("INSERT INTO meta(k, v) VALUES('dead_symbols', ?) "
            "ON CONFLICT(k) DO UPDATE SET v = excluded.v", (json.dumps(merged),))
c.commit()
cur.execute("SELECT COUNT(*) FROM meta WHERE k='dead_symbols'")
print("stored:", cur.fetchone()[0], "tickers")
c.close()
print("sample:", merged[:6])
