# fyers-backfill

A **local historical-data image** for NSE markets, cloned from
[`fyers-collector`](https://github.com/NaniSoft/fyers-collector) but pointed the
other way in time. Instead of a per-minute capture loop, it is a bounded,
resumable batch job that pulls **1-minute OHLCV** from the Fyers v3 API and
writes a tidy **Parquet** dataset to a file share you mount.

- **Cash equities** — every NSE `EQ` symbol in the `NSE_CM` master (≈2,670).
- **Futures** — every live `NSE_FO` future.
- **Options** — every live `NSE_FO` option **and** expired contracts discovered
  through the `/data/history/fno/expired/*` endpoints.
- **As far back as Fyers allows** — from the documented **3 Jul 2017** floor,
  in 100-day chunks, breadth-first by recency so a partial run is still useful.

It reuses the collector's hardened building blocks unchanged: the strict
sliding-window `RateLimiter` (8/s + 190/min), the daily OAuth token service, and
the `FyersClient` transport. Nothing about the live collector changes.

## Dataset layout

One Parquet file per `(resolution, instrument)`, Zstd-compressed, sparse (only
traded minutes — Fyers candles are trade-based), and **merge-on-write** so
re-running a window is idempotent:

```
<root>/
  1min/
    NSE_SBIN-EQ.parquet
    NSE_RELIANCE-EQ.parquet
    NSE_NIFTY26AUGFUT.parquet
    NSE_NIFTY26AUG24800CE.parquet
  1day/                 # when resolutions: ["1", "D"]
    NSE_SBIN-EQ.parquet
  _ledger.db            # resume ledger (SQLite)
  _failures.jsonl       # unresolvable units, one JSON object per line
  _masters/             # cached symbol masters + expired-contract listing
```

Columns:

| column | type | notes |
|---|---|---|
| `symbol` | string | Fyers ticker (`NSE:SBIN-EQ`) |
| `isin` | string | cash equities only |
| `instrument_type` | string | `EQ` / `FUTURE` / `OPTION` |
| `underlying` | string | derivatives only (`NIFTY`, `RELIANCE`) |
| `expiry_epoch` | int64 | derivatives only |
| `strike` | double | options only |
| `option_type` | string | `CE` / `PE` |
| `ts_utc` | int64 | candle epoch (start of the minute), UTC seconds |
| `ist_minute` | string | `yyyy-MM-dd HH:mm` IST |
| `open` `high` `low` `close` | double | raw, **unadjusted** |
| `volume` | int64 | |
| `oi` | int64 | reserved (the wire's 7th field) |
| `resolution` | string | `1` / `D` |

## Quick start (Docker)

```sh
cp .env.example .env          # FYERS_APP_ID, FYERS_SECRET_ID, FYERS_REDIRECT_URI
docker build -t fyers-backfill:local .

# Point the dataset at your share. The container root is /data.
SHARE=/mnt/nas/fyers-data

docker run --rm -v "$SHARE":/data --env-file .env fyers-backfill:local status
docker run --rm -p 8001:8001 -v "$SHARE":/data --env-file .env \
  -e FYERS_REDIRECT_URI=http://<host>:8001/callback fyers-backfill:local login
docker run --rm -v "$SHARE":/data --env-file .env fyers-backfill:local universe
docker run --rm -v "$SHARE":/data --env-file .env fyers-backfill:local pilot
docker run --rm -v "$SHARE":/data --env-file .env fyers-backfill:local backfill
docker run --rm -v "$SHARE":/data --env-file .env fyers-backfill:local update
```

`docker compose` equivalents are in `docker-compose.yml`
(`FYERS_SHARE=/mnt/nas/fyers-data docker compose run --rm backfill backfill`).

### Commands

| command | what it does |
|---|---|
| `login` | serves the OAuth callback and waits for a fresh token (one click/day) |
| `universe` | prints the instrument universe size by kind |
| `pilot [--pilot N]` | fetches N instruments end-to-end before a sweep (default 5) |
| `backfill` | full resumable sweep |
| `update` | newest window per instrument (the daily increment) |
| `status` | ledger + dataset summary, plus the failures count |

### Options

| option | default | meaning |
|---|---|---|
| `--root PATH` | `$FYERS_BACKFILL_ROOT` or `data/backfill` | dataset root (the mounted share) |
| `--config PATH` | `<repo>/config.yaml` | config file (optional; built-in defaults apply) |
| `--env PATH` | `<repo>/.env` | credentials |
| `--data-dir PATH` | `<repo>/data` | where `fyers_access_token.json` lives |
| `--pilot N` | 5 | instrument count for `pilot` |

## Configuration

The `backfill:` section of `config.yaml` (see `config.example.yaml`). Everything
is optional; the defaults are the sensible ones:

```yaml
backfill:
  root: data/backfill         # dataset root; container overrides to /data
  from: "2017-07-03"          # documented 1-minute floor
  to: null                    # null = today (IST)
  chunk_days: 100             # Fyers caps minute resolutions at 100 days/request
  resolutions: ["1"]          # add "D" for daily candles
  include_oi: true
  daily_budget: 90000         # max requests per run (account pool is 100k/day)
  max_minutes: 0              # wall-clock cap per run (0 = unlimited)
  market_hours_only: false    # true = refuse to run 09:15–15:30 IST
  rate_limit: { per_minute: 190, per_second: 8 }
  universe:
    equities: true
    futures: true
    options: true
    expired: true
    underlyings: []           # [] = all; else stems, e.g. [NIFTY, RELIANCE]
```

## How it stays within Fyers' limits

The Fyers caps are **global to the account** (10/s, 200/min, 100,000/day), and
breaching the per-minute cap more than three times in a day blocks the account
until midnight. The live collector draws on the same pool, so:

- Every call goes through the collector's `RateLimiter` — never bypassed.
- `daily_budget` caps each run; the run stops cleanly when it is reached and the
  ledger picks up exactly where it left off next time.
- Set `market_hours_only: true` (or use a second Fyers app/account) to avoid
  competing with production capture.

The initial full sweep is large — roughly `(2026 − 2017) × 365 / 100 ≈ 34`
requests per equity, plus the F&O universe. It is expected to run over several
days, resuming each time; that is the design, not a failure mode.

## Resuming and failures

- The **ledger** (`_ledger.db`) records every `(symbol, resolution, window)` as
  `done` or `failed`. A run skips `done` units, so killing it is always safe.
- A **token that dies mid-run** parks the job (`status` shows `PARKED (auth)`);
  re-run `login`, then re-run the same command.
- Failures are written to `_failures.jsonl` and marked `failed` in the ledger —
  they are retried on the next run, never dropped silently.

## Expired F&O — the honest caveats

`/data/history` does **not** serve expired contracts. Expired futures/options
come from three endpoints the official Python SDK exposes, which this image
ports into `FyersClient`:

```
GET /data/history/fno/expired/expiry-dates?symbol=&range_from=&range_to=&date_format=1
GET /data/history/fno/expired/underlying-symbols?symbol=&expiry_date=YYYY-MM-DD
GET /data/history/fno/expired/historical-data?symbol=&resolution=1&date_format=1&range_from=&range_to=&include_oi=1
```

These endpoints are **not in the public v3 documentation**, so their response
shapes are parsed tolerantly. The discovery pass is cached for 7 days under
`_masters/expired_universe.json`. The expired universe is enormous (every strike
of every expiry across nine years); bound it with `universe.underlyings` if you
do not need all of it. Delisted underlyings that are absent from today's master
cannot be discovered at all.

Prices are **raw and unadjusted** for corporate actions. That is deliberate: raw
can always be adjusted later, adjusted can never be un-adjusted.

## Development

```sh
dotnet test Fyers.slnx -c Release
```

Layout mirrors `fyers-collector`: `Fyers.Core` (client/rate-limiter/storage),
`Fyers.Token` (daily OAuth), and the new `Fyers.Backfill`
(`Instruments/`, `Work/`, `Parquet/`, `Ledger/`, `Failures/`). The only changes
to the cloned code are the ranged `/data/history` overload and the expired-F&O
endpoints in `Fyers.Core`.
