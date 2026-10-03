"""Organize a completed F&O/index pull from the staging area into the ISIN dataset.

    python organize_fno.py            # dry run: mapping stats only
    python organize_fno.py --apply    # move/merge files + write the F&O manifest

Layout (inside data/): equity F&O -> <ISIN>/fno/, index derivatives ->
_INDEX/<STEM>/, unresolvable -> _UNMAPPED/fno/. The cash manifest
(data/_manifest.csv) is NOT touched; F&O inventory lands in _manifest_fno.csv.

Safety: a retried window arrives as a PARTIAL staging file, so an existing
target is never replaced — both files are merged by ts_utc (pull wins),
matching the .NET Organizer's merge-on-write. Cash staging files (-EQ/-BE) are
left alone (the daily chain's .NET Organizer owns those).
"""
import os
import sys
import json
import collections

import pyarrow.parquet as pq

REPO = r"C:\Users\dpven\source\repos\nanisoft\fyers-backfill"
ROOT = os.path.join(REPO, "data")
SRC = os.path.join(ROOT, "backfill", "1min")
MASTERS = os.path.join(ROOT, "backfill", "_masters")
ISIN_MAP = os.path.join(REPO, "eod2_data", "isin_symbol_map.json")

APPLY = "--apply" in sys.argv

INDEX_STEMS = {"NIFTY", "BANKNIFTY", "FINNIFTY", "MIDCPNIFTY", "NIFTYNXT50", "INDIAVIX", "NIFTYFPI"}


def parse_symbol(fname):
    """NSE_SBIN-EQ.parquet -> NSE:SBIN-EQ (the first '_' was the ':' we sanitized)."""
    base = fname[:-len(".parquet")]
    i = base.find("_")
    return base[:i] + ":" + base[i + 1:] if i >= 0 else base


def merge_into(dst, src):
    """Merge-on-write by ts_utc, pull wins (same rule as the .NET Organizer).
    The staging source is consumed — leaving it would double every later run."""
    def rows(path):
        t = pq.read_table(path).to_pylist()
        return {r["ts_utc"]: r for r in t}

    merged = rows(dst)
    merged.update(rows(src))
    keys = sorted(merged)
    table = pa_table([merged[k] for k in keys])
    pq.write_table(table, dst + ".tmp", compression="zstd")
    os.replace(dst + ".tmp", dst)
    os.remove(src)


def pa_table(rows):
    import pyarrow as pa
    names = ["symbol", "isin", "instrument_type", "underlying", "expiry_epoch",
             "strike", "option_type", "ts_utc", "ist_minute",
             "open", "high", "low", "close", "volume", "oi", "resolution"]
    cols = {n: [r.get(n) for r in rows] for n in names}
    return pa.table(cols)


def main():
    with open(ISIN_MAP, encoding="utf-8") as f:
        sym2isin = json.load(f)["sym2isin"]

    with open(os.path.join(MASTERS, "sym_master_fo.json"), encoding="utf-8") as f:
        fo = json.load(f)
    sym_under = {}
    for k, v in fo.items():
        st = v.get("symTicker") or k
        sym_under[st] = v.get("underSym")

    files = [f for f in os.listdir(SRC) if f.endswith(".parquet")]
    fno_plan, index_files, unmapped, cash_skipped = [], [], [], []
    for fname in files:
        symbol = parse_symbol(fname)
        src = os.path.join(SRC, fname)
        if symbol.endswith("-EQ") or symbol.endswith("-BE"):
            cash_skipped.append(fname)          # the .NET daily Organizer owns cash
            continue
        under = sym_under.get(symbol)
        if under is None:
            # Expired contracts are not in the live F&O master; derive the index
            # stem from the ticker prefix (longest stem first: NIFTYNXT50 > NIFTY).
            base = symbol.split(":", 1)[-1]
            for stem in sorted(INDEX_STEMS, key=len, reverse=True):
                if base.startswith(stem):
                    under = stem
                    break
        isin = sym2isin.get(under) if under else None
        if isin:
            fno_plan.append((src, os.path.join(ROOT, isin, "fno"), isin, symbol))
        elif under in INDEX_STEMS:
            index_files.append((src, os.path.join(ROOT, "_INDEX", under), under, symbol))
        else:
            unmapped.append((src, symbol, under))

    overwrites = 0
    if APPLY:
        for src, dest_dir, key, symbol in fno_plan + index_files:
            os.makedirs(dest_dir, exist_ok=True)
            dst = os.path.join(dest_dir, os.path.basename(src))
            if os.path.exists(dst):
                merge_into(dst, src)            # partial re-pull: merge, never replace
                overwrites += 1
            else:
                os.replace(src, dst)

    fno_isins = {p[2] for p in fno_plan}
    idx = collections.Counter(p[2] for p in index_files)
    print(f"staging files          : {len(files)}")
    print(f"  cash (skipped)       : {len(cash_skipped)}")
    print(f"  fno  -> <ISIN>/fno   : {len(fno_plan)} across {len(fno_isins)} ISINs")
    print(f"  index F&O -> _INDEX  : {len(index_files)} {dict(idx)}")
    print(f"  unmapped             : {len(unmapped)}")
    if unmapped:
        print("unmapped sample:", [(s, u) for _, s, u in unmapped[:10]])

    if not APPLY:
        print("\n(dry run — pass --apply to move/merge files)")
        return

    # F&O manifest: a true inventory walk of what is on disk (fno + _INDEX).
    manifest = []
    for entry in sorted(os.listdir(ROOT)):
        epath = os.path.join(ROOT, entry)
        if not os.path.isdir(epath) or entry.startswith("_"):
            continue
        kpath = os.path.join(epath, "fno")
        if os.path.isdir(kpath):
            for f in sorted(os.listdir(kpath)):
                if f.endswith(".parquet"):
                    manifest.append({"isin": entry, "kind": "fno", "symbol": f[:-8],
                                     "path": os.path.join(entry, "fno", f)})
    idx_dir = os.path.join(ROOT, "_INDEX")
    if os.path.isdir(idx_dir):
        for u in sorted(os.listdir(idx_dir)):
            upath = os.path.join(idx_dir, u)
            if not os.path.isdir(upath):
                continue
            for f in sorted(os.listdir(upath)):
                if f.endswith(".parquet"):
                    manifest.append({"isin": "", "kind": "index", "symbol": f[:-8],
                                     "path": os.path.join("_INDEX", u, f)})
    out = os.path.join(ROOT, "_manifest_fno.csv")
    import csv as _csv
    with open(out, "w", newline="", encoding="utf-8") as fh:
        w = _csv.DictWriter(fh, fieldnames=["isin", "kind", "symbol", "path"])
        w.writeheader()
        w.writerows(manifest)
    print(f"\nmerged/overwritten: {overwrites}; manifest lists {len(manifest)} files at {out}")
    if cash_skipped:
        print(f"NOTE: {len(cash_skipped)} cash staging files left in {SRC} for the .NET Organizer")


if __name__ == "__main__":
    main()
