"""Footer-only verification of the by-ISIN dataset (v2 - works on full-history files).

v1 did `pq.read_table(..., columns=["ist_minute"]).to_pylist()` per cash file, which is
1.3B Python strings now that each file spans 2017-2026. v2 reads only parquet footers
(row-group statistics for `ist_minute`) so the whole scan takes seconds.

Reports:
  * September-2026 trading-day coverage per cash file (missing days)
  * per-file / per-ISIN / overall row counts and first-last trading day
  * F&O file counts, empty files, row counts and span (incl. _INDEX trees)
Writes _verify/*.csv + summary.json.
"""
import os
import csv
import json
import datetime as dt
from collections import defaultdict

import pyarrow.parquet as pq

ROOT = r"C:\Users\dpven\fyers-data"
DEST = os.path.join(ROOT, "by-isin")
ISIN_DIR = r"C:\Users\dpven\source\repos\trading\isin"
OUT = os.path.join(ROOT, "_verify")
WINDOW_START = dt.date(2026, 9, 1)
WINDOW_END = dt.date(2026, 9, 24)


def expected_trading_days():
    meta = json.load(open(os.path.join(ISIN_DIR, "meta.json"), encoding="utf-8"))
    holidays = set()
    for k in (meta.get("holidays") or {}):
        try:
            holidays.add(dt.datetime.strptime(k, "%d-%b-%Y").date())
        except ValueError:
            pass
    days = []
    d = WINDOW_START
    while d <= WINDOW_END:
        if d.weekday() < 5 and d not in holidays:
            days.append(d.isoformat())
        d += dt.timedelta(days=1)
    return days


def scan(path):
    """(rows, first_day, last_day, [(rg_lo, rg_hi), ...], dayset_or_None) from the footer.

    C#-written base files (pre-part era) carry no column statistics, so when the footer
    yields nothing we fall back to reading just the ist_minute column and return an exact
    day set for those files.
    """
    pf = pq.ParquetFile(path)
    meta = pf.metadata
    n = meta.num_rows
    if n == 0:
        return 0, None, None, [], None
    try:
        idx = list(pf.schema_arrow.names).index("ist_minute")
    except ValueError:
        return n, None, None, [], None
    ranges, lo_all, hi_all = [], None, None
    for rg in range(meta.num_row_groups):
        st = meta.row_group(rg).column(idx).statistics
        if st is not None and st.has_min_max and isinstance(st.min, str):
            lo, hi = st.min[:10], st.max[:10]
            ranges.append((lo, hi))
            lo_all = lo if lo_all is None or lo < lo_all else lo_all
            hi_all = hi if hi_all is None or hi > hi_all else hi_all
    if ranges:
        return n, lo_all, hi_all, ranges, None

    col = pq.read_table(path, columns=["ist_minute"]).column("ist_minute")
    days = sorted({v[:10] for v in col.to_pylist()})
    if not days:
        return n, None, None, [], set()
    return n, days[0], days[-1], [(days[0], days[-1])], set(days)


def main():
    os.makedirs(OUT, exist_ok=True)
    expected = expected_trading_days()
    exp_set = set(expected)
    print(f"expected trading days in {WINDOW_START}..{WINDOW_END}: {len(expected)}")

    cash_rows, fno_rows = [], []
    isin_fno = defaultdict(lambda: {"files": 0, "rows": 0, "empty": 0, "min": None, "max": None})
    g_lo = g_hi = None
    g_cash_rows = 0
    no_stats = 0

    for isin in sorted(os.listdir(DEST)):
        idir = os.path.join(DEST, isin)
        if not os.path.isdir(idir) or isin.startswith("_"):
            continue
        cash_dir = os.path.join(idir, "cash")
        if os.path.isdir(cash_dir):
            for f in sorted(os.listdir(cash_dir)):
                if not f.endswith(".parquet"):
                    continue
                p = os.path.join(cash_dir, f)
                n, lo, hi, ranges, dayset = scan(p)
                g_cash_rows += n
                if lo and (g_lo is None or lo < g_lo):
                    g_lo = lo
                if hi and (g_hi is None or hi > g_hi):
                    g_hi = hi
                if dayset is not None:
                    present = dayset & exp_set
                    no_stats += 1
                else:
                    present = {e for e in exp_set if any(a <= e <= b for a, b in ranges)}
                missing = sorted(exp_set - present)
                cash_rows.append({
                    "isin": isin, "symbol": f[:-8],
                    "days_present": len(present), "days_expected": len(expected),
                    "missing_days": ";".join(missing),
                    "first_day": lo or "", "last_day": hi or "", "rows": n})
        fno_dir = os.path.join(idir, "fno")
        if os.path.isdir(fno_dir):
            for f in sorted(os.listdir(fno_dir)):
                if not f.endswith(".parquet"):
                    continue
                n, lo, hi, _, _ = scan(os.path.join(fno_dir, f))
                rec = isin_fno[isin]
                rec["files"] += 1
                rec["rows"] += n
                if n == 0:
                    rec["empty"] += 1
                else:
                    if lo:
                        rec["min"] = lo if rec["min"] is None or lo < rec["min"] else rec["min"]
                    if hi:
                        rec["max"] = hi if rec["max"] is None or hi > rec["max"] else rec["max"]

    for isin, rec in sorted(isin_fno.items()):
        fno_rows.append({"isin": isin, **rec})

    index_counts = {}
    idx_root = os.path.join(DEST, "_INDEX")
    if os.path.isdir(idx_root):
        for u in sorted(os.listdir(idx_root)):
            p = os.path.join(idx_root, u)
            if not os.path.isdir(p):
                continue
            files = [f for f in os.listdir(p) if f.endswith(".parquet")]
            rows = lo = hi = None
            for f in files:
                n, a, b, _, _ = scan(os.path.join(p, f))
                rows = (rows or 0) + n
                if a:
                    lo = a if lo is None or a < lo else lo
                if b:
                    hi = b if hi is None or b > hi else hi
            index_counts[u] = {"files": len(files), "rows": rows, "min": lo, "max": hi}

    def write_csv(name, rows, fields):
        with open(os.path.join(OUT, name), "w", newline="", encoding="utf-8") as f:
            w = csv.DictWriter(f, fieldnames=fields)
            w.writeheader()
            w.writerows(rows)

    write_csv("cash_coverage.csv", cash_rows,
              ["isin", "symbol", "days_present", "days_expected", "missing_days",
               "first_day", "last_day", "rows"])
    write_csv("fno_coverage.csv", fno_rows, ["isin", "files", "rows", "empty", "min", "max"])

    complete = [r for r in cash_rows if not r["missing_days"]]
    incomplete = [r for r in cash_rows if r["missing_days"]]
    fno_files = sum(r["files"] for r in fno_rows) + sum(v["files"] for v in index_counts.values())
    fno_empty = sum(r["empty"] for r in fno_rows)
    fno_rows_total = sum(r["rows"] for r in fno_rows) + sum(v["rows"] for v in index_counts.values())

    summary = {
        "expected_trading_days": expected,
        "cash": {"isins": len(cash_rows), "complete": len(complete),
                 "incomplete": len(incomplete), "rows": g_cash_rows,
                 "first_day": g_lo, "last_day": g_hi,
                 "files_without_footer_stats": no_stats},
        "fno": {"isins_with_fno": len(fno_rows), "files": fno_files,
                "empty_files": fno_empty, "rows": fno_rows_total},
        "index": index_counts,
        "incomplete_sample": incomplete[:25],
    }
    json.dump(summary, open(os.path.join(OUT, "summary.json"), "w", encoding="utf-8"), indent=2)

    print(f"\ncash ISINs   : {len(cash_rows)}  complete={len(complete)}  incomplete={len(incomplete)}")
    print(f"cash rows    : {g_cash_rows:,}  span {g_lo} .. {g_hi}  (files read w/o footer stats: {no_stats})")
    print(f"fno ISINs    : {len(fno_rows)}  files={fno_files}  empty={fno_empty}  rows={fno_rows_total:,}")
    print(f"index trees  : {json.dumps(index_counts)}")
    if incomplete:
        print("incomplete cash sample:")
        for r in incomplete[:15]:
            print(f"  {r['symbol']:24} {r['days_present']}/{r['days_expected']} "
                  f"missing={r['missing_days']} span={r['first_day']}..{r['last_day']} rows={r['rows']}")
    print(f"\nwrote {OUT}\\summary.json, cash_coverage.csv, fno_coverage.csv")


if __name__ == "__main__":
    main()
