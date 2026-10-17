"""Aggregate the 1-min cash parquet into daily bars and cache them.

Reads every by-isin/<ISIN>/cash/*.parquet, aggregates to one row per IST trading day
(open=first, high=max, low=min, close=last, volume=sum, bars=count) and writes a single
compact parquet so EOD validation can be re-run without touching the 8 GB of 1-min data.
"""
import os
import sys
import time

import numpy as np
import pyarrow as pa
import pyarrow.parquet as pq

ROOT = r"C:\Users\dpven\fyers-data"
DEST = os.path.join(ROOT, "by-isin")
OUT = os.path.join(ROOT, "_verify", "our_eod.parquet")

IST_OFFSET = 19800
DAY = 86400
EPOCH = np.datetime64("1970-01-01", "s")

COLS = ["ts_utc", "open", "high", "low", "close", "volume"]
SCHEMA = pa.schema([
    ("isin", pa.string()), ("symbol", pa.string()), ("date", pa.string()),
    ("open", pa.float64()), ("high", pa.float64()), ("low", pa.float64()),
    ("close", pa.float64()), ("volume", pa.int64()), ("bars", pa.int32()),
    ("first_min", pa.string()), ("last_min", pa.string()),
])


def day_to_date(day_ids):
    return ((EPOCH + (day_ids.astype("int64") * DAY).astype("timedelta64[s]"))
            .astype("datetime64[D]").astype(str))


def aggregate(path):
    """-> dict of numpy arrays for one file, or None if empty."""
    t = pq.read_table(path, columns=COLS)
    n = t.num_rows
    if n == 0:
        return None
    ts = t.column("ts_utc").to_numpy(zero_copy_only=False).astype(np.int64)
    o = t.column("open").to_numpy(zero_copy_only=False)
    h = t.column("high").to_numpy(zero_copy_only=False)
    lo = t.column("low").to_numpy(zero_copy_only=False)
    c = t.column("close").to_numpy(zero_copy_only=False)
    v = t.column("volume").to_numpy(zero_copy_only=False).astype(np.int64)
    del t

    day = (ts + IST_OFFSET) // DAY
    starts = np.flatnonzero(np.concatenate(([True], day[1:] != day[:-1])))
    if starts.size == 0:
        return None
    ends = np.append(starts[1:], n)
    return {
        "date": day_to_date(day[starts]),
        "open": o[starts], "high": np.maximum.reduceat(h, starts),
        "low": np.minimum.reduceat(lo, starts), "close": c[ends - 1],
        "volume": np.add.reduceat(v, starts),
        "bars": (ends - starts).astype(np.int32),
        "first_ts": ts[starts], "last_ts": ts[ends - 1],
        "monotonic": bool(np.all(np.diff(ts) > 0)),
        "dups": int(n - np.unique(ts).size),
    }


def hhmm(ts):
    ist = ts + IST_OFFSET
    h = ((ist // 3600) % 24).astype("U2")
    m = ((ist % 3600) // 60).astype("U2")
    return np.char.add(np.char.add(h, ":"), np.char.zfill(m, 2))


def main():
    only = set(sys.argv[1:])
    isins = sorted(d for d in os.listdir(DEST)
                   if os.path.isdir(os.path.join(DEST, d)) and not d.startswith("_"))
    if only:
        isins = [i for i in isins if i in only]
    print(f"ISINs: {len(isins)}", flush=True)

    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    writer = pq.ParquetWriter(OUT, SCHEMA, compression="zstd")
    total = 0
    nonmono = dupfiles = 0
    t0 = time.time()
    for k, isin in enumerate(isins, 1):
        cdir = os.path.join(DEST, isin, "cash")
        if not os.path.isdir(cdir):
            continue
        per_file = []
        for f in sorted(os.listdir(cdir)):
            if not f.endswith(".parquet"):
                continue
            try:
                a = aggregate(os.path.join(cdir, f))
            except Exception as e:
                print(f"  READ FAIL {isin}/{f}: {e!r}", flush=True)
                continue
            if a is None:
                continue
            if not a.pop("monotonic"):
                nonmono += 1
            if a.pop("dups"):
                dupfiles += 1
            a["symbol"] = f[:-8]
            per_file.append(a)
        if not per_file:
            continue

        if len(per_file) == 1:
            a = per_file[0]
            dates, syms = a["date"], [a["symbol"]] * a["date"].size
            cols = {k: a[k] for k in ("open", "high", "low", "close", "volume", "bars")}
            first_min, last_min = hhmm(a["first_ts"]), hhmm(a["last_ts"])
        else:
            all_dates = np.concatenate([a["date"] for a in per_file])
            order = np.argsort(all_dates, kind="stable")
            dates = all_dates[order]
            syms = np.concatenate([np.full(a["date"].size, a["symbol"]) for a in per_file])[order]
            stacked = {k: np.concatenate([a[k] for a in per_file])[order]
                       for k in ("open", "high", "low", "close", "volume", "bars")}
            fm = np.concatenate([hhmm(a["first_ts"]) for a in per_file])[order]
            lm = np.concatenate([hhmm(a["last_ts"]) for a in per_file])[order]
            # collapse duplicate dates, keeping the session with more bars
            starts = np.flatnonzero(np.concatenate(([True], dates[1:] != dates[:-1])))
            ends = np.append(starts[1:], dates.size)
            gb = stacked["bars"]
            best = np.array([s + int(np.argmax(gb[s:e])) for s, e in zip(starts, ends)])
            dates, syms = dates[best], syms[best]
            cols = {k: stacked[k][best] for k in stacked}
            first_min, last_min = fm[best], lm[best]

        writer.write_table(pa.Table.from_arrays(
            [pa.array([isin] * dates.size), pa.array(list(syms)), pa.array(list(dates))] +
            [pa.array(cols[k]) for k in ("open", "high", "low", "close", "volume", "bars")] +
            [pa.array(list(first_min)), pa.array(list(last_min))],
            schema=SCHEMA))
        total += dates.size
        if k % 100 == 0:
            el = time.time() - t0
            print(f"  {k}/{len(isins)} ISINs, {total} days, {k/el:.1f} ISIN/s, "
                  f"eta {(len(isins)-k)/(k/el)/60:.0f} min, nonmono={nonmono}, dupfiles={dupfiles}",
                  flush=True)
    writer.close()
    print(f"wrote {OUT}: {total} daily bars, non-monotonic files={nonmono}, "
          f"files with duplicate minutes={dupfiles}, {time.time()-t0:.0f}s", flush=True)


if __name__ == "__main__":
    main()
