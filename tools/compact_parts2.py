"""Memory-frugal pyarrow compactor (v2).

Folds _parts/<res>/<SYMBOL>/*.parquet (plus any existing base <res>/<SYMBOL>.parquet)
into one de-duplicated, ts_utc-sorted base file.  v1 died on MemoryError because it
round-tripped every symbol through pandas (2-3x peak) while the box was loaded.
v2 concatenates in Arrow, releases pools between symbols, and retries once.
"""
import gc
import glob
import os
import sys
import time

import numpy as np
import pyarrow as pa
import pyarrow.parquet as pq

ROOT = r"C:\Users\dpven\fyers-data"
PARTS = os.path.join(ROOT, "_parts")

pa.set_cpu_count(2)
POOL = pa.default_memory_pool()


def _free():
    gc.collect()
    POOL.release_unused()


def _read(path):
    return pq.read_table(path, use_threads=False, memory_map=True)


def compact_symbol(sym_dir):
    res = os.path.basename(os.path.dirname(sym_dir))          # e.g. "1min"
    sym = os.path.basename(sym_dir)
    base = os.path.join(ROOT, res, sym + ".parquet")
    files = sorted(glob.glob(os.path.join(sym_dir, "*.parquet")))
    if not files:
        return 0

    schema = None
    tables = []
    if os.path.exists(base):
        t = _read(base)
        schema = t.schema
        tables.append(t)
    for f in files:
        t = _read(f)
        if schema is None:
            schema = t.schema
        elif not t.schema.equals(schema):
            t = t.cast(schema)
        tables.append(t)

    t = pa.concat_tables(tables)
    del tables
    _free()

    t = t.sort_by([("ts_utc", "ascending")])
    _free()

    a = t.column("ts_utc").combine_chunks().to_numpy(zero_copy_only=False)
    if len(a) == 0:
        sel = np.empty(0, dtype=np.int64)
    else:
        starts = np.flatnonzero(np.concatenate(([True], a[1:] != a[:-1])))
        sel = np.append(starts[1:], len(a) - 1).astype(np.int64)
    if len(sel) != len(a):
        t = t.take(pa.array(sel))
    del a, sel
    _free()

    os.makedirs(os.path.dirname(base), exist_ok=True)
    tmp = base + ".tmp"
    pq.write_table(t, tmp, compression="zstd", row_group_size=256_000)
    os.replace(tmp, base)
    n = t.num_rows
    del t
    _free()

    for f in files:
        os.remove(f)
    try:
        os.rmdir(sym_dir)
    except OSError:
        pass
    return n


def main():
    if not os.path.isdir(PARTS):
        print("no _parts", flush=True)
        return
    sym_dirs = []
    for res in sorted(os.listdir(PARTS)):
        rp = os.path.join(PARTS, res)
        if not os.path.isdir(rp):
            continue
        for s in sorted(os.listdir(rp)):
            d = os.path.join(rp, s)
            if os.path.isdir(d) and glob.glob(os.path.join(d, "*.parquet")):
                sym_dirs.append(d)
    total_parts = sum(len(glob.glob(os.path.join(d, "*.parquet"))) for d in sym_dirs)
    print(f"symbols to compact: {len(sym_dirs)} (parts: {total_parts})", flush=True)

    done = rows = fails = 0
    t0 = time.time()
    for d in sym_dirs:
        try:
            rows += compact_symbol(d)
            done += 1
        except MemoryError:
            _free()
            try:
                rows += compact_symbol(d)
                done += 1
            except Exception as e:
                fails += 1
                print(f"FAIL {d}: {e!r}", flush=True)
        except Exception as e:
            fails += 1
            print(f"FAIL {d}: {e!r}", flush=True)
        if done % 25 == 0:
            el = max(time.time() - t0, 1e-9)
            rate = done / el
            print(f"compact: {done}/{len(sym_dirs)} symbols, {rows} rows, "
                  f"{rate:.2f} sym/s, eta {(len(sym_dirs)-done)/rate/60:.0f} min, fails={fails}",
                  flush=True)
    print(f"compact done: {done} symbols, {rows} rows, fails={fails}, "
          f"{time.time()-t0:.0f}s", flush=True)


if __name__ == "__main__":
    main()
