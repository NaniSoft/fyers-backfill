"""Symbol-days with bars outside the 09:15-15:30 IST cash session.

Times are compared NUMERICALLY: first_min/last_min are 'HH:MM' strings, and a
string comparison gets this wrong ("9:44" > "15:30" lexicographically), which
inflated an earlier count of this to 24,270.
"""
import pandas as pd

O = r"C:\Users\dpven\fyers-data\_verify"
SESSION_OPEN, SESSION_CLOSE, PREOPEN_END = 9 * 60 + 15, 15 * 60 + 30, 9 * 60 + 8

MUHURAT = {"2017-10-19", "2018-11-07", "2019-10-27", "2020-11-14", "2021-11-04",
           "2022-10-24", "2023-11-12", "2024-01-22", "2024-11-01", "2025-10-21"}


def to_min(s):
    p = s.astype(str).str.split(":")
    return p.str[0].astype(int) * 60 + p.str[1].astype(int)


o = pd.read_parquet(O + r"\our_eod.parquet")
f, l = to_min(o.first_min), to_min(o.last_min)
mask = ((l > SESSION_CLOSE) | (f < SESSION_OPEN)) & ~(f < PREOPEN_END)
out = o[mask].copy()
out["reason"] = ["muhurat_session" if d in MUHURAT else "after_close_block" for d in out.date]
out[["isin", "symbol", "date", "first_min", "last_min", "bars", "open", "high",
    "low", "close", "volume", "reason"]].sort_values(["date", "symbol"]).to_csv(
    O + r"\out_of_session_days.csv", index=False)

print(f"out_of_session_days.csv: {len(out):,} symbol-days on {out.date.nunique()} dates")
print(out.reason.value_counts().to_string())
print(f"\npre-open (09:00-09:08) days excluded as legitimate: "
      f"{int(((f < PREOPEN_END) & (o.first_min.notna())).sum()):,} symbol-days")
