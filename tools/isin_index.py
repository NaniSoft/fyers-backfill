"""Build by-isin/_isin_index.csv: one row per ISIN with its symbol name history
(from_date/to_date), cash coverage, and F&O file counts."""
import os, csv, json

ROOT = r"C:\Users\dpven\fyers-data"
DEST = os.path.join(ROOT, "by-isin")
OUT = os.path.join(ROOT, "_verify")
ISIN_DIR = r"C:\Users\dpven\source\repos\trading\isin"

hist = json.load(open(os.path.join(ISIN_DIR, "isin_symbol_map.json"), encoding="utf-8"))["isin2hist"]

cash = {}
for r in csv.DictReader(open(os.path.join(OUT, "cash_coverage.csv"), encoding="utf-8")):
    cash[r["isin"]] = r
fno = {}
for r in csv.DictReader(open(os.path.join(OUT, "fno_coverage.csv"), encoding="utf-8")):
    fno[r["isin"]] = r

rows = []
for isin, events in sorted(hist.items()):
    syms = [e["symbol"] for e in events]
    firsts = [e["from_date"] for e in events if e.get("from_date")]
    lasts = [e["to_date"] for e in events if e.get("to_date")]
    c = cash.get(isin)
    f = fno.get(isin)
    rows.append({
        "isin": isin,
        "symbols": ";".join(syms),
        "first_seen": min(firsts) if firsts else "",
        "last_seen": max(lasts) if lasts else "",
        "renames": len(syms),
        "cash_days": f"{c['days_present']}/{c['days_expected']}" if c else "",
        "cash_rows": c["rows"] if c else "",
        "fno_files": f["files"] if f else 0,
        "fno_rows": f["rows"] if f else 0,
        "has_cash": "yes" if c else "no",
        "has_fno": "yes" if f else "no",
    })

with open(os.path.join(DEST, "_isin_index.csv"), "w", newline="", encoding="utf-8") as fh:
    w = csv.DictWriter(fh, fieldnames=list(rows[0].keys()))
    w.writeheader()
    w.writerows(rows)

renamed = [r for r in rows if r["renames"] > 1]
print(f"wrote {DEST}\\_isin_index.csv  ({len(rows)} ISINs)")
print(f"ISINs with name changes : {len(renamed)}")
for r in renamed[:10]:
    print(f"  {r['isin']} {r['symbols']}  {r['first_seen']}..{r['last_seen']}")
print(f"ISINs with cash+data    : {sum(1 for r in rows if r['cash_rows'])}")
print(f"ISINs with F&O          : {sum(1 for r in rows if r['has_fno']=='yes')}")
