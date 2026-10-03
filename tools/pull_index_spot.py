"""Pull 1-minute index spot candles from Fyers /data/history into the ISIN dataset.

Index spot is not part of the backfill universe (NSE_CM/NSE_FO masters carry no
index rows), so this is a small standalone pass. Writes the CandleRowDto schema
under data/_INDEX/<STEM>/spot.parquet — the derivatives organizer puts index
F&O beside it as data/_INDEX/<STEM>/<contract>.parquet.

Run AFTER a fresh login (reads data/fyers_access_token.json), BEFORE the big
backfill: the 190/min Fyers rate cap is account-global.

Usage: python pull_index_spot.py [probe]   # probe = one symbol, one day
"""
import json
import sys
import time
import datetime as dt
import urllib.parse
import urllib.request
from pathlib import Path

REPO = Path(r"C:\Users\dpven\source\repos\nanisoft\fyers-backfill")
OUT_ROOT = REPO / "data" / "_INDEX"

INDEXES = {
    "NIFTY":      "NSE:NIFTY50-INDEX",
    "BANKNIFTY":  "NSE:NIFTYBANK-INDEX",
    "FINNIFTY":   "NSE:FINNIFTY-INDEX",
    "MIDCPNIFTY": "NSE:MIDCPNIFTY-INDEX",
}
FLOOR = dt.date(2017, 7, 3)          # Fyers' documented 1-minute floor
CHUNK_DAYS = 100                      # Fyers caps minute resolutions at 100 days
IST = dt.timezone(dt.timedelta(hours=5, minutes=30))
HISTORY = "https://api-t1.fyers.in/data/history"


def token():
    d = json.loads((REPO / "data" / "fyers_access_token.json").read_text())
    app = (REPO / ".env").read_text().split("FYERS_APP_ID=")[1].splitlines()[0].strip()
    return f"{app}:{d['access_token']}"


def fetch(auth, symbol, day_from, day_to):
    qs = urllib.parse.urlencode({
        "symbol": symbol, "resolution": "1", "date_format": "1",
        "range_from": day_from.strftime("%Y-%m-%d"),
        "range_to": day_to.strftime("%Y-%m-%d"), "include_oi": "1",
    })
    req = urllib.request.Request(f"{HISTORY}?{qs}",
                                 headers={"Authorization": auth,
                                          "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64)"})
    for attempt in range(5):
        try:
            with urllib.request.urlopen(req, timeout=30) as r:
                return json.loads(r.read())
        except urllib.error.HTTPError as e:
            if e.code != 429 or attempt == 4:
                raise
            time.sleep(30 * (attempt + 1))    # per-minute cap: back off and retry


def sanitize(candles):
    """The CandleSanitizer rules: drop non-positive prices, clamp high/low,
    zero sentinel volumes (2^32-family wrap-arounds). Fyers candles are
    positional: [epoch, open, high, low, close, volume(, oi)]."""
    out = {}
    for c in candles:
        ts = int(c[0])                        # epoch seconds, candle start
        o, h, l, cl = (float(c[1]), float(c[2]), float(c[3]), float(c[4]))
        if min(o, h, l, cl) <= 0:
            continue
        v = int(c[5])
        if v < 0 or v > 100_000_000:
            v = 0
        out[ts] = (ts, max(o, h, l, cl), min(o, h, l, cl), o, cl, v)
    return [out[k] for k in sorted(out)]


def write_parquet(stem, symbol, rows):
    import pyarrow as pa
    import pyarrow.parquet as pq

    out_dir = OUT_ROOT / stem
    out_dir.mkdir(parents=True, exist_ok=True)
    table = pa.table({
        "symbol": [symbol] * len(rows),
        "isin": [None] * len(rows),
        "instrument_type": ["INDEX"] * len(rows),
        "underlying": [None] * len(rows),
        "expiry_epoch": [None] * len(rows),
        "strike": [None] * len(rows),
        "option_type": [None] * len(rows),
        "ts_utc": [r[0] for r in rows],
        "ist_minute": [dt.datetime.fromtimestamp(r[0], IST).strftime("%Y-%m-%d %H:%M") for r in rows],
        "open": [r[3] for r in rows],
        "high": [r[1] for r in rows],
        "low": [r[2] for r in rows],
        "close": [r[4] for r in rows],
        "volume": [r[5] for r in rows],
        "oi": [None] * len(rows),
        "resolution": ["1"] * len(rows),
    })
    pq.write_table(table, out_dir / "spot.parquet", compression="zstd")


def main():
    probe = len(sys.argv) > 1 and sys.argv[1] == "probe"
    auth = token()
    today = dt.date.today()
    targets = [("NIFTY", INDEXES["NIFTY"])] if probe else list(INDEXES.items())

    for stem, symbol in targets:
        if probe:
            windows = [(today - dt.timedelta(days=7), today)]
        else:
            windows, cur = [], FLOOR
            while cur <= today:
                windows.append((cur, min(cur + dt.timedelta(days=CHUNK_DAYS - 1), today)))
                cur += dt.timedelta(days=CHUNK_DAYS)

        all_rows, served = [], 0
        for day_from, day_to in windows:
            resp = fetch(auth, symbol, day_from, day_to)
            if resp.get("s") != "ok":
                print(f"  {day_from}..{day_to}: {resp.get('code')} {resp.get('message','')}")
                continue
            served += 1
            all_rows.extend(sanitize(resp.get("candles", [])))
            time.sleep(0.4)               # ~150/min — the 190/min cap is account-global

        print(f"{stem} {symbol}: {len(all_rows)} rows, {served}/{len(windows)} windows served")
        if all_rows and not probe:
            write_parquet(stem, symbol, all_rows)
            first = dt.datetime.fromtimestamp(all_rows[0][0], IST)
            last = dt.datetime.fromtimestamp(all_rows[-1][0], IST)
            print(f"  -> {OUT_ROOT / stem / 'spot.parquet'}  {first} .. {last}")


if __name__ == "__main__":
    main()
