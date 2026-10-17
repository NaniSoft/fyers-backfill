"""Which ISINs have whole name eras missing because we only pulled the current ticker?"""
import json, os
import pandas as pd

O = r"C:\Users\dpven\fyers-data\_verify"
TRADING = r"C:\Users\dpven\source\repos\trading\isin"
ours = pd.read_parquet(O + r"\our_eod.parquet")
hist = json.load(open(os.path.join(TRADING, "isin_symbol_map.json"), encoding="utf-8"))["isin2hist"]
cover = ours.groupby("isin")["date"].agg(["min", "max", "size"]).rename(
    columns={"min": "our_first", "max": "our_last", "size": "our_days"})

rows = []
for isin, names in hist.items():
    if len(names) < 2:
        continue
    c = cover.loc[isin] if isin in cover.index else None
    for n in names:
        f, t = n["from_date"], n["to_date"]
        if c is None:
            covered, days = 0, 0
        else:
            lo = max(f, c.our_first)
            hi = min(t, c.our_last)
            covered = 1 if lo <= hi else 0
            days = int(ours.loc[(ours["isin"] == isin) & (ours["date"] >= lo) & (ours["date"] <= hi)].shape[0])
        rows.append({"isin": isin, "symbol": n["symbol"], "from_date": f, "to_date": t,
                     "touched_by_our_data": covered, "our_days_in_era": days})

d = pd.DataFrame(rows)
d.to_csv(O + r"\name_era_coverage.csv", index=False)
multi = d.groupby("isin").size()
multi = multi[multi > 1]
gap = d[d["isin"].isin(multi.index)].groupby("isin").agg(
    eras=("symbol", "size"), eras_covered=("touched_by_our_data", "sum"))
gap = gap[gap.eras_covered < gap.eras]
print(f"ISINs with a name change: {len(multi)}")
print(f"ISINs where at least one name era is entirely missing from our data: {len(gap)}")
print("\nworst (most missing eras):")
det = d[d["isin"].isin(gap.index)].sort_values(["isin", "from_date"])
for isin in gap.sort_values("eras_covered").head(8).index:
    sub = det[det["isin"] == isin]
    c = cover.loc[isin] if isin in cover.index else None
    span = f"our data {c.our_first}..{c.our_last}" if c is not None else "no data at all"
    print(f"  {isin}  ({span})")
    for r in sub.itertuples():
        mark = "OK " if r.touched_by_our_data else "GAP"
        print(f"      {mark} {r.symbol:22} {r.from_date}..{r.to_date}  our_days={r.our_days_in_era}")
tot_gap_eras = int((d[d['isin'].isin(gap.index)].touched_by_our_data == 0).sum())
print(f"\ntotal fully-missing name eras: {tot_gap_eras}")
