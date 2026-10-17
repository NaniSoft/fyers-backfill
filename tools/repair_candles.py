"""Repair the existing 1-min parquet in place, applying the same rules as
Fyers.Core.Fyers.CandleSanitizer (so repaired files match what a fresh pull produces):

  * drop a bar whose open/high/low/close is <= 0
  * high = max(o,h,l,c), low = min(o,h,l,c)
  * volume outside [0, VOL_MAX] -> 0   (wrapped-negative sentinel, e.g. 2^32*100-3600)

Row order and the file schema are preserved; a file is only rewritten when it
actually needs it, and the swap is atomic (tmp + os.replace).

    python repair_candles.py [VOL_MAX] [only_these.txt]

The optional second argument is a file of "ISIN|SYMBOL" lines to restrict the
sweep to (used for a targeted second pass over the stragglers).
"""
import os
import sys
import time

import numpy as np
import pyarrow as pa
import pyarrow.parquet as pq

ROOT = r"C:\Users\dpven\fyers-data"
DEST = os.path.join(ROOT, "by-isin")
# Same constant as Fyers.Core.Fyers.CandleSanitizer.MaxSaneVolume. See its remark
# for the empirical basis (p99.999 of 32M liquid minute volumes is 50.8M shares).
VOL_MAX = 100_000_000
FIELDS = ("open", "high", "low", "close", "volume")
POOL = pa.default_memory_pool()


def repair(path):
    """-> (rows_in, rows_out, dropped, clamped, vol_fixed) or None if unreadable."""
    pf = pq.ParquetFile(path)
    schema = pf.schema_arrow
    if pf.metadata.num_rows == 0:
        return 0, 0, 0, 0, 0
    for f in FIELDS:
        if f not in schema.names:
            return None

    n_in = pf.metadata.num_rows
    dropped = clamped = vol_fixed = 0
    tmp = path + ".repair"

    # First pass: is anything wrong at all?  Cheap enough to do while reading.
    batches = []
    need = False
    for rg in range(pf.metadata.num_row_groups):
        t = pf.read_row_group(rg)
        o = t.column("open").to_numpy(zero_copy_only=False)
        h = t.column("high").to_numpy(zero_copy_only=False)
        lo = t.column("low").to_numpy(zero_copy_only=False)
        c = t.column("close").to_numpy(zero_copy_only=False)
        v = t.column("volume").to_numpy(zero_copy_only=False)
        valid = (o > 0) & (h > 0) & (lo > 0) & (c > 0)
        bad_v = (v < 0) | (v > VOL_MAX)
        hi = np.maximum(np.maximum(h, o), np.maximum(lo, c))
        lw = np.minimum(np.minimum(lo, o), np.minimum(h, c))
        if not valid.all() or bad_v.any() or (hi != h).any() or (lw != lo).any():
            need = True
            break
        batches.append(None)      # row group is clean

    if not need:
        return n_in, n_in, 0, 0, 0

    writer = pq.ParquetWriter(tmp, schema, compression="zstd")
    n_out = 0
    for rg in range(pf.metadata.num_row_groups):
        t = pf.read_row_group(rg)
        o = t.column("open").to_numpy(zero_copy_only=False)
        h = t.column("high").to_numpy(zero_copy_only=False)
        lo = t.column("low").to_numpy(zero_copy_only=False)
        c = t.column("close").to_numpy(zero_copy_only=False)
        v = t.column("volume").to_numpy(zero_copy_only=False)

        valid = (o > 0) & (h > 0) & (lo > 0) & (c > 0)
        hi = np.maximum(np.maximum(h, o), np.maximum(lo, c))
        lw = np.minimum(np.minimum(lo, o), np.minimum(h, c))
        bad_v = (v < 0) | (v > VOL_MAX)

        clamped += int(((hi != h) | (lw != lo)).sum())
        vol_fixed += int(bad_v.sum())
        v = np.where(bad_v, np.int64(0), v)
        dropped += int((~valid).sum())

        keep = np.flatnonzero(valid)
        cols = []
        for name, field in zip(schema.names, schema):
            if name == "open":
                cols.append(pa.array(o[keep], type=field.type))
            elif name == "high":
                cols.append(pa.array(hi[keep], type=field.type))
            elif name == "low":
                cols.append(pa.array(lw[keep], type=field.type))
            elif name == "close":
                cols.append(pa.array(c[keep], type=field.type))
            elif name == "volume":
                cols.append(pa.array(v[keep], type=field.type))
            else:
                col = t.column(name)
                cols.append(col if keep.size == len(o) else col.take(pa.array(keep)))
        out = pa.Table.from_arrays(cols, schema=schema)
        writer.write_table(out)
        n_out += out.num_rows
        del t, o, h, lo, c, v
        POOL.release_unused()

    writer.close()
    pf.close()                      # Windows will not replace a file that is still open
    os.replace(tmp, path)
    return n_in, n_out, dropped, clamped, vol_fixed


def main():
    only = None
    if len(sys.argv) > 2:
        only = set()
        for line in open(sys.argv[2], encoding="utf-8"):
            line = line.strip()
            if line:
                only.add(line)

    files = []
    for isin in sorted(os.listdir(DEST)):
        d = os.path.join(DEST, isin, "cash")
        if not os.path.isdir(d):
            continue
        for f in sorted(os.listdir(d)):
            if not f.endswith(".parquet"):
                continue
            if only is not None and f"{isin}|{f[:-8]}" not in only:
                continue
            files.append(os.path.join(d, f))
    print(f"vol_max={VOL_MAX} files: {len(files)}", flush=True)

    t0 = time.time()
    files_done = dirty = 0
    tot_in = tot_out = tot_drop = tot_clamp = tot_vol = 0
    for p in files:
        try:
            r = repair(p)
        except Exception as e:
            print(f"FAIL {p}: {e!r}", flush=True)
            r = None
        if r:
            n_in, n_out, dr, cl, vf = r
            tot_in += n_in
            tot_out += n_out
            tot_drop += dr
            tot_clamp += cl
            tot_vol += vf
            if dr or cl or vf:
                dirty += 1
        files_done += 1
        if files_done % 100 == 0:
            el = time.time() - t0
            print(f"  {files_done}/{len(files)} files, {dirty} repaired, "
                  f"rows {tot_in:,}->{tot_out:,}, dropped={tot_drop} clamped={tot_clamp} "
                  f"vol_fixed={tot_vol}, {files_done/el:.1f} files/s, "
                  f"eta {(len(files)-files_done)/(files_done/el)/60:.0f} min", flush=True)

    print(f"repair done: {files_done} files, {dirty} rewritten, rows "
          f"{tot_in:,} -> {tot_out:,} (dropped {tot_drop}, clamped {tot_clamp}, "
          f"volume_fixed {tot_vol}), {time.time()-t0:.0f}s", flush=True)


if __name__ == "__main__":
    main()
