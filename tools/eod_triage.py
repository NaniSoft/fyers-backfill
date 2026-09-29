"""Per-ISIN triage of the EOD validation + markdown report."""
import json, os, collections
import numpy as np, pandas as pd

O = r"C:\Users\dpven\fyers-data\_verify"
TRADING = r"C:\Users\dpven\source\repos\trading\isin"
ours = pd.read_parquet(O + r"\our_eod.parquet")
sm = pd.read_csv(O + r"\eod_isin_summary.csv")
det = pd.read_csv(O + r"\eod_day_anomalies.csv", low_memory=False)
prof = pd.read_csv(O + r"\eod_ratio_profile.csv")

# ---- our-side structural signals straight off the daily cache -------------
g = ours.groupby("isin")
sig = pd.DataFrame({
    "days": g.size(),
    "sentinel_vol_days": g.apply(lambda d: int((d.volume > 1e9).sum()), include_groups=False),
    "nonpositive_days": g.apply(lambda d: int(((d.open <= 0) | (d.high <= 0) |
                                               (d.low <= 0) | (d.close <= 0)).sum()), include_groups=False),
    "sparse_days_lt30": g.apply(lambda d: int((d.bars < 30).sum()), include_groups=False),
    "onebar_days": g.apply(lambda d: int((d.bars == 1).sum()), include_groups=False),
    "first_day": g.date.min(), "last_day": g.date.max(),
}).reset_index()

t = sm.merge(sig, on="isin", how="outer", suffixes=("", "_o"))
t = t.merge(prof[["isin", "med_close_ratio", "exact_close_pct", "exact_ohlc_pct", "med_vol_ratio"]],
            on="isin", how="left")


def classify(r):
    f = []
    if pd.notna(r.med_close_ratio) and not (0.9 <= r.med_close_ratio <= 1.1):
        f.append("basis_mismatch")
    if r.sentinel_vol_days > 0:
        f.append("sentinel_volume")
    if r.get("internal_ours", 0) > 0:
        f.append("bad_bars")
    if r.nonpositive_days > 0:
        f.append("nonpositive_price")
    if r.get("missing_day", 0) > 0:
        f.append("missing_days")
    if r.get("price_open", 0) == 0 and r.get("price_high", 0) == 0 and r.get("price_low", 0) == 0 \
            and r.get("price_close", 0) > 0:
        f.append("close_only_diff")
    if r.sparse_days_lt30 > 0:
        f.append("sparse_sessions")
    return "|".join(f) if f else "clean"


t["flags"] = t.apply(classify, axis=1)
t["close_only_diff_pct"] = np.where(t.joined_days > 0,
                                    t.price_close / t.joined_days * 100, np.nan)
t["vol_shortfall_pct"] = (1 - t.med_vol_ratio) * 100
cols = ["isin", "our_symbols", "ref_stems", "flags", "days", "our_first", "our_last",
        "ref_days", "joined_days", "missing_day", "extra_day", "price_open", "price_high",
        "price_low", "price_close", "close_only_diff_pct", "volume", "vol_shortfall_pct",
        "internal_ours", "internal_ref", "partial_session", "sparse_days_lt30", "onebar_days",
        "sentinel_vol_days", "nonpositive_days", "med_close_ratio", "exact_close_pct",
        "exact_ohlc_pct"]
t[cols].sort_values(["flags", "isin"]).to_csv(O + r"\eod_isin_triage.csv", index=False)

print("=== ISIN triage")
fl = t["flags"].value_counts()
for k, v in fl.items():
    print(f"  {k:60} {v}")
print("\nclean ISINs:", int(((t["flags"] == "clean").sum())), "of", len(t))

print("\n=== our-side defect totals (ISINs affected / days affected)")
for c, lab in (("sentinel_vol_days", "sentinel volume days"), ("nonpositive_days", "non-positive OHLC days"),
               ("onebar_days", "single-bar days"), ("sparse_days_lt30", "days with <30 bars"),
               ("missing_day", "days the reference has and we don't"), ("extra_day", "days we have and the reference doesn't")):
    col = t[c] if c in t.columns else t[c + "_o"]
    print(f"  {lab:44} isins={int((col > 0).sum()):5}  days={int(col.fillna(0).sum()):7}")

print("\n=== missing_day structure (top 10 ISINs)")
md = det[det.kind == "missing_day"]
top = md.groupby("isin").size().sort_values(ascending=False).head(10)
hist = json.load(open(os.path.join(TRADING, "isin_symbol_map.json"), encoding="utf-8"))["isin2hist"]
for isin in top.index:
    dates = sorted(md.loc[md["isin"] == isin, "date"])
    blocks, cur = [], [dates[0]]
    for a, b in zip(dates, dates[1:]):
        if (pd.Timestamp(b) - pd.Timestamp(a)).days <= 4:
            cur.append(b)
        else:
            blocks.append((cur[0], cur[-1], len(cur)))
            cur = [b]
    blocks.append((cur[0], cur[-1], len(cur)))
    h = hist.get(isin, [])
    names = " -> ".join(f"{x['symbol']}({x['from_date']}..{x['to_date']})" for x in h[:4])
    print(f"  {isin} miss={len(dates):4} blocks={blocks[:4]} names={names}")

print("\n=== basis-mismatch ISINs (constant price factor vs reference)")
bm = t[t["flags"].str.contains("basis_mismatch")][["isin", "our_symbols", "med_close_ratio", "days"]]
bm = bm.assign(factor=lambda d: (1 / d.med_close_ratio).round(3))
print(bm.sort_values("factor").head(12).to_string(index=False))
print(f"  ... {len(bm)} ISINs total; factor range "
      f"{bm.factor.min()} .. {bm.factor.max()}")

print("\n=== close-only difference, quantified (ISINs with exact O/H/L)")
co = t[t["flags"].str.contains("close_only_diff")]
print(f"  ISINs: {len(co)}  median share of days with a close diff: "
      f"{co.close_only_diff_pct.median():.1f}%  (O/H/L all exact)")
print("  median volume shortfall vs reference: %.3f%%" % co.vol_shortfall_pct.median())
print("\nwrote", O + r"\eod_isin_triage.csv")
