"""Organize the Fyers parquet dataset into one folder per ISIN (cash + F&O),
using the shared isin_symbol_map.json, then verify September coverage.

    python organize_by_isin.py            # dry run: mapping stats only
    python organize_by_isin.py --apply    # move files + write verification
"""
import os
import sys
import json
import shutil
import collections

import pyarrow.parquet as pq

ROOT = r"C:\Users\dpven\fyers-data"
SRC = os.path.join(ROOT, "1min")
DEST = os.path.join(ROOT, "by-isin")
ISIN_DIR = r"C:\Users\dpven\source\repos\trading\isin"
MASTERS = os.path.join(ROOT, "_masters")

APPLY = "--apply" in sys.argv
CLEAN = "--clean" in sys.argv          # wipe by-isin/ first (full rebuild)
WINDOW_START, WINDOW_END = "2026-09-01", "2026-09-24"

INDEX_STEMS = {"NIFTY", "BANKNIFTY", "FINNIFTY", "MIDCPNIFTY", "NIFTYNXT50", "INDIAVIX", "NIFTYFPI"}


def load_json(path):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def parse_symbol(fname):
    """NSE_SBIN-EQ.parquet -> NSE:SBIN-EQ (the first '_' was the ':' we sanitized)."""
    base = fname[:-len(".parquet")]
    i = base.find("_")
    return base[:i] + ":" + base[i + 1:] if i >= 0 else base


def main():
    shared = load_json(os.path.join(ISIN_DIR, "isin_symbol_map.json"))
    sym2isin = shared["sym2isin"]
    isin2hist = shared["isin2hist"]

    fo = load_json(os.path.join(MASTERS, "sym_master_fo.json"))
    sym_under = {}
    for k, v in fo.items():
        st = v.get("symTicker") or k
        sym_under[st] = v.get("underSym")

    cm = load_json(os.path.join(MASTERS, "sym_master_cm.json"))
    cm_isin = {}
    for k, v in cm.items():
        if str(v.get("exSeries", "")).upper() == "EQ":
            cm_isin[v.get("symTicker") or k] = v.get("isin")

    files = [f for f in os.listdir(SRC) if f.endswith(".parquet")]
    plan, index_files, unmapped = [], [], []
    for fname in files:
        symbol = parse_symbol(fname)
        src = os.path.join(SRC, fname)
        if symbol.endswith("-EQ") or symbol.endswith("-BE"):
            # -BE = BE-series cash (the fallback used for symbols Fyers won't serve as -EQ)
            raw = symbol[len("NSE:"):]
            ticker = raw.rsplit("-", 1)[0]
            isin = cm_isin.get(symbol) or sym2isin.get(raw) or sym2isin.get(ticker)
            if isin:
                plan.append((src, os.path.join(DEST, isin, "cash"), isin, "cash", symbol))
            else:
                unmapped.append((src, symbol, "cash", ticker))
        else:
            under = sym_under.get(symbol)
            if under is None:
                # Expired contracts are not in the F&O master; derive the index stem
                # from the ticker prefix (longest stem first: NIFTYNXT50 before NIFTY).
                base = symbol.split(":", 1)[-1]
                for stem in sorted(INDEX_STEMS, key=len, reverse=True):
                    if base.startswith(stem):
                        under = stem
                        break
            isin = sym2isin.get(under) if under else None
            if isin:
                plan.append((src, os.path.join(DEST, isin, "fno"), isin, "fno", symbol))
            elif under in INDEX_STEMS:
                index_files.append((src, os.path.join(DEST, "_INDEX", under), under, "fno", symbol))
            else:
                unmapped.append((src, symbol, "fno", under))

    isins = {p[2] for p in plan}
    cash_isins = {p[2] for p in plan if p[3] == "cash"}
    fno_isins = {p[2] for p in plan if p[3] == "fno"}
    print(f"parquet files      : {len(files)}")
    print(f"  cash -> ISIN     : {sum(1 for p in plan if p[3]=='cash')}")
    print(f"  fno  -> ISIN     : {sum(1 for p in plan if p[3]=='fno')}")
    print(f"  index F&O        : {len(index_files)} (folders under _INDEX/)")
    print(f"  unmapped         : {len(unmapped)}")
    print(f"distinct ISINs     : {len(isins)} (cash {len(cash_isins)}, fno {len(fno_isins)})")
    if unmapped:
        print("unmapped sample:", unmapped[:10])
    idx = collections.Counter(u[2] for u in index_files)
    print("index underlyings  :", dict(idx))

    if not APPLY:
        print("\n(dry run — pass --apply to move files and write verification)")
        return

    if CLEAN and os.path.isdir(DEST):
        n = sum(len(fs) for _, _, fs in os.walk(DEST))
        print(f"cleaning {DEST} ({n} files)...")
        shutil.rmtree(DEST)

    moved = over = 0
    manifest = []

    def place(src, dest_dir, isin, kind, symbol):
        nonlocal moved, over
        os.makedirs(dest_dir, exist_ok=True)
        dst = os.path.join(dest_dir, os.path.basename(src))
        if os.path.exists(dst):
            os.remove(dst)          # shutil.move would raise on an existing dst
            over += 1
        shutil.move(src, dst)
        manifest.append({"isin": isin, "kind": kind, "symbol": symbol,
                         "path": os.path.relpath(dst, ROOT)})
        moved += 1

    for src, dest_dir, isin, kind, symbol in plan + index_files:
        place(src, dest_dir, isin, kind, symbol)
    for src, symbol, kind, key in unmapped:
        place(src, os.path.join(DEST, "_UNMAPPED", kind), "", kind, symbol)

    # Build the manifest by walking DEST so it is a true inventory of what is on
    # disk -- not just of this run's moves (files may already have been organised).
    manifest = []
    for entry in sorted(os.listdir(DEST)):
        epath = os.path.join(DEST, entry)
        if not os.path.isdir(epath):
            continue
        if entry == "_INDEX":
            for u in sorted(os.listdir(epath)):
                upath = os.path.join(epath, u)
                for f in sorted(os.listdir(upath)):
                    if f.endswith(".parquet"):
                        manifest.append({"isin": "", "kind": "index", "symbol": f[:-8],
                                         "path": os.path.relpath(os.path.join(upath, f), ROOT)})
        elif entry == "_UNMAPPED":
            for kind in sorted(os.listdir(epath)):
                kpath = os.path.join(epath, kind)
                for f in sorted(os.listdir(kpath)):
                    if f.endswith(".parquet"):
                        manifest.append({"isin": "", "kind": "unmapped-" + kind, "symbol": f[:-8],
                                         "path": os.path.relpath(os.path.join(kpath, f), ROOT)})
        else:
            for kind in ("cash", "fno"):
                kpath = os.path.join(epath, kind)
                if not os.path.isdir(kpath):
                    continue
                for f in sorted(os.listdir(kpath)):
                    if f.endswith(".parquet"):
                        manifest.append({"isin": entry, "kind": kind, "symbol": f[:-8],
                                         "path": os.path.relpath(os.path.join(kpath, f), ROOT)})

    with open(os.path.join(DEST, "_manifest.csv"), "w", newline="", encoding="utf-8") as f:
        import csv as _csv
        w = _csv.DictWriter(f, fieldnames=["isin", "kind", "symbol", "path"])
        w.writeheader()
        w.writerows(manifest)
    print(f"\nmoved {moved} files into {DEST} ({over} overwritten); "
          f"manifest lists {len(manifest)} files at {DEST}\\_manifest.csv")


if __name__ == "__main__":
    main()
