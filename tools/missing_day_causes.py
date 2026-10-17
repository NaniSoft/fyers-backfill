"""Split the 22,488 'reference has a day, we do not' into actionable causes:
  (a) the day falls inside a name era we never requested (fixable by pulling that ticker)
  (b) genuine holes in Fyers history for a ticker we did request
and emit the exact per-symbol windows worth re-pulling.
"""
import json, os, collections
import pandas as pd

O = r"C:\Users\dpven\fyers-data\_verify"
TRADING = r"C:\Users\dpven\source\repos\trading\isin"
W0, W1 = "2017-07-03", "2026-09-24"

ours = pd.read_parquet(O + r"\our_eod.parquet")
det = pd.read_csv(O + r"\eod_day_anomalies.csv", low_memory=False)
hist = json.load(open(os.path.join(TRADING, "isin_symbol_map.json"), encoding="utf-8"))["isin2hist"]
tri = pd.read_csv(O + r"\eod_isin_triage.csv")
sym_of = {r.isin: str(r.our_symbols) for r in tri.itertuples()}

md = det[det.kind == "missing_day"]
rows = []
for isin, g in md.groupby("isin"):
    days = sorted(g["date"])
    eras = hist.get(isin, [])
    our_syms = [s.replace("NSE:", "").replace("-EQ", "").replace("-BE", "")
                for s in sym_of.get(isin, "").split(";") if s]
    for d in days:
        era = next((e for e in eras if e["from_date"] <= d <= e["to_date"]), None)
        sym = era["symbol"] if era else None
        if sym is not None and sym not in our_syms:
            cause = "never_requested_ticker"       # we hold another name for this ISIN
        elif sym is not None:
            cause = "requested_but_absent"         # we asked for this ticker, Fyers had nothing
        else:
            cause = "no_era_mapping"
        rows.append({"isin": isin, "date": d, "era_symbol": sym,
                     "in_our_files": ";".join(our_syms), "cause": cause})

d = pd.DataFrame(rows)
d.to_csv(O + r"\missing_day_causes.csv", index=False)
print("missing days:", len(d))
print(d.cause.value_counts().to_string())

print("\n### (a) never-requested ticker — grouped into pullable eras")
a = d[d.cause == "never_requested_ticker"]
g = a.groupby(["era_symbol", "isin"]).agg(days=("date", "size"), first=("date", "min"),
                                          last=("date", "max")).reset_index()
g = g.sort_values("days", ascending=False)
print(f"tickers involved: {g.era_symbol.nunique()}  ISINs: {g['isin'].nunique()}  total days: {int(g.days.sum())}")
print(g.head(20).to_string(index=False))
g.to_csv(O + r"\pullable_historical_eras.csv", index=False)

print("\n### (b) requested but Fyers returned nothing — real holes")
b = d[d.cause == "requested_but_absent"]
h = b.groupby(["isin", "in_our_files"]).agg(days=("date", "size"), first=("date", "min"),
                                            last=("date", "max")).reset_index()
h = h.sort_values("days", ascending=False)
print(f"ISINs: {h['isin'].nunique()}  total days: {int(h.days.sum())}")
print(h.head(20).to_string(index=False))
h.to_csv(O + r"\genuine_holes.csv", index=False)

print("\n### hole size distribution (requested-but-absent, per ISIN)")
print(h.days.describe().round(1).to_string())
print("\nno_era_mapping days:", int((d.cause == "no_era_mapping").sum()))
