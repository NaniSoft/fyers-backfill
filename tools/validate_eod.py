"""Validate every cash ISIN's EOD bar against the BhavDesk eod2 reference set.

Inputs
  ours     : _verify/our_eod.parquet  (built by build_our_eod.py from the 1-min data)
  reference: <eod2_data>/daily/<symbol>.csv  +  isin_symbol_map.json (stem -> ISIN)

Checks per ISIN, over the overlap of the two histories
  * day-set differences   : ref day missing from ours, our day absent from ref
  * OHLC differences      : tick-aware tolerance, raw diffs always recorded
  * volume differences    : relative tolerance
  * internal consistency  : low <= min(open,close) <= max(open,close) <= high, all > 0
  * session completeness  : bar count per day (375 = full NSE 09:15-15:30 session)

Outputs (under _verify/)
  eod_day_anomalies.csv, eod_isin_summary.csv, eod_unmatched_ref.csv,
  eod_validation_summary.json
"""
import json
import os
import time
from collections import defaultdict

import numpy as np
import pandas as pd

ROOT = r"C:\Users\dpven\fyers-data"
REF = r"C:\Users\dpven\Downloads\BhavDesk-main\eod2\src\eod2_data"
REF_DAILY = os.path.join(REF, "daily")
OUT = os.path.join(ROOT, "_verify")
OURS = os.path.join(OUT, "our_eod.parquet")

FULL_SESSION = 375        # NSE cash session 09:15..15:30 IST = 375 one-minute bars
PARTIAL_BARS = 370
VOL_REL_TOL = 0.02
VOL_ABS_FLOOR = 1000
TICK_BANDS = [250.0, 500.0, 2000.0, 5000.0]
TICK_VALS = [0.05, 0.1, 0.5, 1.0]
FIELDS = ("open", "high", "low", "close")


def ticks_of(price):
    """Vectorised NSE equity tick size."""
    return np.select(
        [np.abs(price) < TICK_BANDS[0], np.abs(price) < TICK_BANDS[1],
         np.abs(price) < TICK_BANDS[2], np.abs(price) < TICK_BANDS[3]],
        TICK_VALS, default=5.0)


def load_ours():
    df = pd.read_parquet(OURS)
    for c in ("open", "high", "low", "close", "volume"):
        df[c] = df[c].astype("float64")
    df["bars"] = df["bars"].astype("int32")
    return df


def build_stem_map():
    m = json.load(open(os.path.join(REF, "isin_symbol_map.json"), encoding="utf-8"))
    s2i = {k.upper(): v for k, v in m["sym2isin"].items()}
    extra = {}
    ip = os.path.join(REF, "isin.csv")
    if os.path.exists(ip):
        try:
            t = pd.read_csv(ip, usecols=["ISIN", "SYMBOL"]).dropna()
            for isin, sym in t.itertuples(index=False):
                extra.setdefault(str(sym).strip().upper(), str(isin).strip())
        except Exception as e:
            print(f"  (isin.csv unusable: {e!r})", flush=True)

    def resolve(stem):
        u = stem.upper()
        cands = [u]
        if u.endswith("_SME"):
            cands.append(u[:-4])
        cands.append(u.replace("_", "").replace("-", "").replace("&", ""))
        if u.endswith("_SME"):
            cands.append(u[:-4].replace("-", "").replace("&", ""))
        for c in cands:
            if c in s2i:
                return s2i[c], "sym2isin"
        for c in cands:
            if c in extra:
                return extra[c], "isin.csv"
        return None, None

    return resolve


def load_reference(resolve):
    by_isin, stems, unmatched = {}, defaultdict(list), []
    files = sorted(f for f in os.listdir(REF_DAILY) if f.endswith(".csv"))
    for f in files:
        stem = f[:-4]
        isin, how = resolve(stem)
        if isin is None:
            unmatched.append({"stem": stem, "file": f, "reason": "no ISIN mapping"})
            continue
        try:
            d = pd.read_csv(os.path.join(REF_DAILY, f),
                            usecols=["Date", "Open", "High", "Low", "Close", "Volume", "Series"])
        except Exception as e:
            unmatched.append({"stem": stem, "file": f, "reason": f"read error {e!r}"})
            continue
        d = d.rename(columns={"Date": "date", "Open": "r_open", "High": "r_high",
                              "Low": "r_low", "Close": "r_close", "Volume": "r_vol"})
        d["date"] = d["date"].astype(str).str.slice(0, 10)
        for c in ("r_open", "r_high", "r_low", "r_close", "r_vol"):
            d[c] = pd.to_numeric(d[c], errors="coerce")
        d = d.dropna(subset=["date"]).drop_duplicates(subset=["date"], keep="last")
        stems[isin].append(stem)
        if isin not in by_isin or len(d) > len(by_isin[isin]):
            d = d.copy()
            d["stem"] = stem
            d["mapped_by"] = how
            by_isin[isin] = d
    return by_isin, stems, unmatched


def internal_bad(o, h, l, c):
    hi = np.maximum(o, c)
    lo = np.minimum(o, c)
    return (h < l) | (h < hi - 1e-9) | (l > lo + 1e-9) | (h <= 0) | (l <= 0) | (o <= 0) | (c <= 0)


def main():
    t0 = time.time()
    print("loading our EOD...", flush=True)
    ours = load_ours()
    ours_by_isin = {k: v.set_index("date").sort_index() for k, v in ours.groupby("isin", sort=False)}
    print(f"  ours: {len(ours)} daily bars, {len(ours_by_isin)} ISINs", flush=True)

    print("loading reference...", flush=True)
    resolve = build_stem_map()
    ref, stems, unmatched = load_reference(resolve)
    print(f"  ref: {sum(len(v) for v in ref.values())} rows, {len(ref)} ISINs, "
          f"{len(unmatched)} unmatched files ({time.time()-t0:.0f}s)", flush=True)

    frames = []
    summary = []
    tick_dist = defaultdict(int)
    worst_close_rel = 0.0

    our_isins = set(ours_by_isin)
    common = sorted(our_isins & set(ref))

    for n, isin in enumerate(common, 1):
        o = ours_by_isin[isin]
        r = ref[isin].set_index("date").sort_index()
        o_first, o_last = o.index[0], o.index[-1]
        r_first, r_last = r.index[0], r.index[-1]

        r_win = r.loc[(r.index >= o_first) & (r.index <= o_last)]
        o_win = o.loc[(o.index >= r_first) & (o.index <= r_last)]
        missing = sorted(set(r_win.index) - set(o.index))
        extra = sorted(set(o_win.index) - set(r_win.index))
        j = o.join(r_win, how="inner")
        cnt = defaultdict(int)
        worst_close = worst_close_rel_i = worst_vol_rel = 0.0

        ov = o["volume"].to_numpy(dtype=float)
        ob_ = o[["open", "high", "low", "close"]].to_numpy(dtype=float)

        # ---- our own structural checks (reference-independent) ----
        bo = internal_bad(ob_[:, 0], ob_[:, 1], ob_[:, 2], ob_[:, 3])
        if bo.any():
            idx = np.flatnonzero(bo)
            cnt["internal_ours"] = idx.size
            frames.append(pd.DataFrame({
                "isin": isin, "date": o.index[idx], "kind": "internal_ours",
                "detail": "our OHLC inconsistent",
                "our_open": ob_[idx, 0], "our_high": ob_[idx, 1],
                "our_low": ob_[idx, 2], "our_close": ob_[idx, 3]}))

        bars = o["bars"].to_numpy()
        bp = np.flatnonzero(bars < PARTIAL_BARS)
        if bp.size:
            cnt["partial_session"] = bp.size
            frames.append(pd.DataFrame({
                "isin": isin, "date": o.index[bp], "kind": "partial_session",
                "detail": [f"only {int(bars[i])} of {FULL_SESSION} bars" for i in bp],
                "bars": bars[bp],
                "first_min": o["first_min"].to_numpy()[bp],
                "last_min": o["last_min"].to_numpy()[bp]}))

        zv = np.flatnonzero(ov <= 0)
        if zv.size:
            cnt["zero_volume"] = zv.size
            frames.append(pd.DataFrame({
                "isin": isin, "date": o.index[zv], "kind": "zero_volume",
                "detail": "zero volume", "volume": ov[zv]}))

        if missing:
            cnt["missing_day"] = len(missing)
            frames.append(pd.DataFrame({
                "isin": isin, "date": missing, "kind": "missing_day",
                "detail": "ref has day, we have none",
                "ref_close": r_win.loc[missing, "r_close"].to_numpy()}))
        if extra:
            cnt["extra_day"] = len(extra)
            frames.append(pd.DataFrame({
                "isin": isin, "date": extra, "kind": "extra_day",
                "detail": "we have day, ref does not",
                "our_close": o_win.loc[extra, "close"].to_numpy(),
                "bars": o_win.loc[extra, "bars"].to_numpy()}))

        if len(j):
            rb = r_win[["r_open", "r_high", "r_low", "r_close"]].to_numpy(dtype=float)
            br = internal_bad(rb[:, 0], rb[:, 1], rb[:, 2], rb[:, 3])
            if br.any():
                idx = np.flatnonzero(br)
                cnt["internal_ref"] = idx.size
                frames.append(pd.DataFrame({
                    "isin": isin, "date": r_win.index[idx], "kind": "internal_ref",
                    "detail": "reference OHLC inconsistent",
                    "ref_open": rb[idx, 0], "ref_high": rb[idx, 1],
                    "ref_low": rb[idx, 2], "ref_close": rb[idx, 3]}))

            for field in FIELDS:
                a = j[field].to_numpy(dtype=float)
                b = j["r_" + field].to_numpy(dtype=float)
                diff = np.abs(a - b)
                tick = ticks_of(b)
                tol = np.maximum(2.0 * tick, 0.0015 * np.abs(b))
                idx = np.flatnonzero(diff > tol)
                if idx.size:
                    cnt[f"price_{field}"] = idx.size
                    dd = diff[idx]
                    rr = np.where(b[idx] != 0, dd / np.maximum(np.abs(b[idx]), 1e-9), 0.0)
                    frames.append(pd.DataFrame({
                        "isin": isin, "date": j.index[idx], "kind": f"price_{field}",
                        "detail": [f"ours={a[i]:.4f} ref={b[i]:.4f} diff={dd[k]:.4f} "
                                   f"tol={tol[i]:.4f} ticks={dd[k]/max(tick[i],1e-9):.1f}"
                                   for k, i in enumerate(idx)],
                        f"our_{field}": a[idx], f"ref_{field}": b[idx],
                        "abs_diff": dd, "tolerance": tol[idx], "rel_diff": rr}))
                if field == "close":
                    worst_close = float(np.nanmax(diff))
                    worst_close_rel_i = float(np.nanmax(rr))
                    worst_close_rel = max(worst_close_rel, worst_close_rel_i)
                    t = diff / np.maximum(tick, 1e-9)
                    for lo, hi, lab in ((0, 1.0001, "0-1"), (1, 2.0001, "1-2"),
                                        (2, 5.0001, "2-5"), (5, 20.0001, "5-20"),
                                        (20, float("inf"), ">20")):
                        tick_dist[lab] += int(((t > lo) & (t <= hi)).sum())

            a = j["volume"].to_numpy(dtype=float)
            b = j["r_vol"].to_numpy(dtype=float)
            vdiff = np.abs(a - b)
            vrel = np.where(b > 0, vdiff / np.maximum(b, 1), 0.0)
            idx = np.flatnonzero((vrel > VOL_REL_TOL) & (vdiff > VOL_ABS_FLOOR))
            if idx.size:
                cnt["volume"] = idx.size
                frames.append(pd.DataFrame({
                    "isin": isin, "date": j.index[idx], "kind": "volume",
                    "detail": [f"ours={int(a[i])} ref={int(b[i])} rel={vrel[i]:.4f}"
                               for i in idx],
                    "volume": a[idx], "ref_volume": b[idx], "rel_diff": vrel[idx]}))
                worst_vol_rel = float(np.nanmax(vrel[idx]))

        summary.append({
            "isin": isin,
            "our_symbols": ";".join(sorted(set(ours.loc[ours["isin"] == isin, "symbol"]))),
            "ref_stems": ";".join(stems.get(isin, [])),
            "our_first": o_first, "our_last": o_last, "our_days": len(o),
            "ref_first": r_first, "ref_last": r_last, "ref_days": len(r),
            "joined_days": len(j),
            "ref_days_before_our_start": int((r.index < o_first).sum()),
            "missing_day": cnt["missing_day"], "extra_day": cnt["extra_day"],
            "price_open": cnt["price_open"], "price_high": cnt["price_high"],
            "price_low": cnt["price_low"], "price_close": cnt["price_close"],
            "volume": cnt["volume"], "partial_session": cnt["partial_session"],
            "zero_volume": cnt["zero_volume"],
            "internal_ours": cnt["internal_ours"], "internal_ref": cnt["internal_ref"],
            "worst_close_abs": round(worst_close, 4),
            "worst_close_pct": round(worst_close_rel_i * 100, 4),
            "worst_vol_pct": round(worst_vol_rel * 100, 3),
        })
        if n % 300 == 0:
            print(f"  compared {n}/{len(common)} ISINs ({time.time()-t0:.0f}s)", flush=True)

    DET_COLS = ["isin", "date", "kind", "detail", "our_open", "our_high", "our_low",
                "our_close", "ref_open", "ref_high", "ref_low", "ref_close",
                "abs_diff", "tolerance", "rel_diff", "volume", "ref_volume", "bars",
                "first_min", "last_min"]
    det = pd.concat(frames, ignore_index=True) if frames else pd.DataFrame(columns=DET_COLS)
    for c in DET_COLS:
        if c not in det.columns:
            det[c] = np.nan
    det = det[DET_COLS].sort_values(["kind", "isin", "date"])
    det.to_csv(os.path.join(OUT, "eod_day_anomalies.csv"), index=False)
    sm = pd.DataFrame(summary)
    sm.to_csv(os.path.join(OUT, "eod_isin_summary.csv"), index=False)
    if unmatched:
        pd.DataFrame(unmatched).to_csv(os.path.join(OUT, "eod_unmatched_ref.csv"), index=False)

    by_kind = det["kind"].value_counts().to_dict() if len(det) else {}
    flag_cols = ["price_open", "price_high", "price_low", "price_close", "volume",
                 "missing_day", "extra_day", "internal_ours", "partial_session",
                 "zero_volume"]
    clean = int((sm[flag_cols] == 0).all(axis=1).sum()) if len(sm) else 0
    out = {
        "our_isins": len(our_isins), "ref_isins": len(ref), "compared_isins": len(summary),
        "ref_only_isins": len(set(ref) - our_isins), "our_only_isins": len(our_isins - set(ref)),
        "our_daily_bars": int(len(ours)),
        "anomaly_rows": int(len(det)),
        "anomalies_by_kind": {k: int(v) for k, v in sorted(by_kind.items(), key=lambda x: -x[1])},
        "isins_with_zero_anomalies": clean,
        "close_diff_tick_distribution": {k: int(v) for k, v in tick_dist.items()},
        "worst_close_pct_any_isin": round(worst_close_rel * 100, 4),
        "unmatched_ref_files": len(unmatched),
        "seconds": round(time.time() - t0, 1),
    }
    json.dump(out, open(os.path.join(OUT, "eod_validation_summary.json"), "w", encoding="utf-8"),
              indent=2, default=str)
    print(json.dumps(out, indent=2, default=str))


if __name__ == "__main__":
    main()
