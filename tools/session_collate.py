"""Session classification with NUMERIC time arithmetic.

first_min/last_min are 'HH:MM' strings; comparing them as strings is wrong
("9:44" > "15:29" lexicographically), which inflated the earlier
out-of-session count. Everything here converts to minutes since midnight.
"""
import os
import numpy as np
import pandas as pd

O = r"C:\Users\dpven\fyers-data\_verify"
SESSION_OPEN, SESSION_CLOSE = 9 * 60 + 15, 15 * 60 + 30
PREOPEN_END = 9 * 60 + 8

MUHURAT = {
    "2017-10-19": "Muhurat (Diwali) - not available from Fyers (3 symbols)",
    "2018-11-07": "Muhurat (Diwali)",
    "2019-10-27": "Muhurat (Diwali)",
    "2020-11-14": "Muhurat (Diwali)",
    "2021-11-04": "Muhurat (Diwali)",
    "2022-10-24": "Muhurat (Diwali)",
    "2023-11-12": "Muhurat (Diwali)",
    "2024-01-22": "Special one-hour session - not available from Fyers",
    "2024-11-01": "Muhurat (Diwali)",
    "2025-10-21": "Muhurat (Diwali) - Fyers serves it at 13:45-14:45 IST",
}


def to_min(s):
    parts = s.astype(str).str.split(":")
    return parts.str[0].astype(int) * 60 + parts.str[1].astype(int)


o = pd.read_parquet(O + r"\our_eod.parquet")
o["f"] = to_min(o.first_min)
o["l"] = to_min(o.last_min)
o["after_close"] = o.l > SESSION_CLOSE
o["before_open"] = o.f < SESSION_OPEN
o["preopen_only"] = o.f < PREOPEN_END          # 09:00-09:08 pre-open session
o["outside"] = o.after_close & ~o.preopen_only

print(f"symbol-days: {len(o):,}")
print(f"  starting before 09:15          : {int(o.before_open.sum()):>7,}"
      f"   (of which the 09:00-09:08 pre-open session: {int(o.preopen_only.sum()):,})")
print(f"  ending after 15:30             : {int(o.after_close.sum()):>7,}")
print(f"  genuinely outside the session  : {int(o.outside.sum()):>7,}"
      f"  ({100*o.outside.sum()/len(o):.2f}%)")
print(f"  ending after 16:00 (post-close): {int((o.l > 16*60).sum()):,}")

out = o[o.outside]
print(f"\ngenuinely-outside symbol-days on {out.date.nunique()} dates")
per = out.groupby("date").agg(symbols=("symbol", "size"), bars=("bars", "median"),
                              first=("first_min", "min"), last=("last_min", "max"))
print(per.sort_values("symbols", ascending=False).head(20).to_string())
known = per[per.index.isin(MUHURAT)]
other = per[~per.index.isin(MUHURAT)]
print(f"\n  on known Muhurat dates        : {int(known.symbols.sum()):,} symbol-days "
      f"({per.index.isin(MUHURAT).sum()} dates)")
print(f"  on other dates                : {int(other.symbols.sum()):,} symbol-days "
      f"({other.shape[0]} dates)")

# ---------- corrected session calendar ---------------------------------------
g = o.groupby("date")
cal = pd.DataFrame({
    "symbols": g.size(),
    "med_bars": g.bars.median(),
    "min_bars": g.bars.min(),
    "max_bars": g.bars.max(),
    "earliest_min": g.f.min(),
    "latest_min": g.l.max(),
    "outside_days": g.outside.sum(),
    "preopen_days": g.preopen_only.sum(),
})


def hhmm(m):
    m = int(m)
    return f"{m//60:02d}:{m%60:02d}"


cal["earliest"] = cal.earliest_min.map(hhmm)
cal["latest"] = cal.latest_min.map(hhmm)


def classify(r):
    if r.name in MUHURAT:
        return "muhurat"
    if r.symbols >= 200 and r.outside_days / r.symbols > 0.5:
        return "after_hours_session"
    if r.outside_days > 0:
        return "after_hours_minor"
    if r.symbols >= 200 and r.preopen_days / r.symbols > 0.5:
        return "pre_open_session"
    if r.med_bars < 250:
        return "thin_median"
    return "normal"


cal["class"] = cal.apply(classify, axis=1)
cal.index.name = "date"
cal = cal.reset_index()
cal["dow"] = pd.to_datetime(cal.date).dt.day_name()
cal["note"] = cal.date.map(MUHURAT).fillna("")
cal = cal.drop(columns=["earliest_min", "latest_min"])
cal.to_csv(O + r"\session_calendar.csv", index=False)

print("\nsession_calendar.csv by class:")
print(cal["class"].value_counts().to_string())
print("\nafter_hours_session dates:")
print(cal[cal["class"] == "after_hours_session"][
    ["date", "dow", "symbols", "med_bars", "earliest", "latest", "outside_days"]].to_string(index=False))
print("\npre_open_session dates (first 6):")
print(cal[cal["class"] == "pre_open_session"][
    ["date", "dow", "symbols", "med_bars", "earliest", "latest", "preopen_days"]].head(6).to_string(index=False))
print("\nafter_hours_minor dates:", int((cal["class"] == "after_hours_minor").sum()),
      "covering", f"{int(cal[cal['class']=='after_hours_minor'].outside_days.sum()):,}", "symbol-days")
