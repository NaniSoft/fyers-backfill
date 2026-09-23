using System.Text;

namespace Fyers.Core.Storage;

/// <summary>
/// SQLite DDL copied VERBATIM from src/storage.py (parity reference).
///
/// <see cref="DailySchema"/> is the day-file schema (``DAILY_SCHEMA``): the
/// per-minute firehose tables ``option_chain`` + ``quotes`` + ``snapshot_runs``
/// plus ``candles_1m`` (post-close backfill), with every CREATE INDEX. Column
/// names, order, types, constraints and the inline SQL comments are
/// byte-identical to the Python source so a day file written by either
/// implementation reads identically from the other.
///
/// <see cref="SummarySchema"/> is the live summary db schema
/// (``SUMMARY_SCHEMA``: daily_summary / holidays / premarket_cues /
/// constituents / candles_runs). Carried here as text for the EOD task; the
/// python Storage applies it at construction, the port applies it later.
/// </summary>
public static class Ddl
{
    // src/storage.py DAILY_SCHEMA — verbatim (whitespace and comments included).
    public const string DailySchema = """
CREATE TABLE IF NOT EXISTS option_chain (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    ts_utc INTEGER NOT NULL,
    ist_minute TEXT NOT NULL,
    symbol TEXT NOT NULL,
    underlying TEXT NOT NULL,
    expiry_epoch INTEGER NOT NULL,
    strike_price REAL NOT NULL,
    option_type TEXT NOT NULL,           -- CE / PE
    ltp REAL, ltpch REAL, ltpchp REAL,
    bid REAL, ask REAL,
    oi INTEGER, oich INTEGER, oichp REAL, prev_oi INTEGER, volume INTEGER,
    iv REAL, delta REAL, gamma REAL, theta REAL, vega REAL
);
CREATE INDEX IF NOT EXISTS idx_oc_ts ON option_chain(ts_utc);
CREATE INDEX IF NOT EXISTS idx_oc_lookup ON option_chain(underlying, expiry_epoch, ts_utc, strike_price, option_type);

CREATE TABLE IF NOT EXISTS quotes (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    ts_utc INTEGER NOT NULL,
    ist_minute TEXT NOT NULL,
    symbol TEXT NOT NULL,
    instrument_type TEXT NOT NULL,       -- FUTURE / SPOT / VIX / CASH
    underlying TEXT,
    expiry_epoch INTEGER,
    ltp REAL, bid REAL, ask REAL, bid_size INTEGER, ask_size INTEGER,
    oi INTEGER, volume INTEGER,
    ch REAL, chp REAL, prev_close REAL, open REAL, high REAL, low REAL,
    spread REAL, atp REAL
);
CREATE INDEX IF NOT EXISTS idx_q_ts ON quotes(ts_utc);
CREATE INDEX IF NOT EXISTS idx_q_sym ON quotes(symbol, ts_utc);

CREATE TABLE IF NOT EXISTS snapshot_runs (
    ts_utc INTEGER PRIMARY KEY,
    ist_minute TEXT NOT NULL,
    n_legs_options INTEGER,
    n_quotes INTEGER,
    fetch_ms INTEGER,
    errors INTEGER,
    token_valid INTEGER,
    note TEXT
);

CREATE TABLE IF NOT EXISTS candles_1m (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    ts_utc INTEGER NOT NULL,
    ist_minute TEXT NOT NULL,
    symbol TEXT NOT NULL,
    instrument_type TEXT,                -- OPT / FUT / SPOT / VIX / CASH
    underlying TEXT,
    expiry_epoch INTEGER,
    strike_price REAL,
    option_type TEXT,                    -- CE / PE (OPT only)
    open REAL, high REAL, low REAL, close REAL,
    volume INTEGER, oi INTEGER
);
CREATE UNIQUE INDEX IF NOT EXISTS idx_candles_sym_ts ON candles_1m(symbol, ts_utc);
CREATE INDEX IF NOT EXISTS idx_candles_ts ON candles_1m(ts_utc);
""";

    // src/storage.py SUMMARY_SCHEMA — verbatim. Not applied by Storage on
    // construction (summary tables are deferred to the EOD task in the port).
    public const string SummarySchema = """
CREATE TABLE IF NOT EXISTS daily_summary (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    date TEXT NOT NULL,
    underlying TEXT NOT NULL,
    spot_open REAL, spot_high REAL, spot_low REAL, spot_close REAL,
    vix_open REAL, vix_high REAL, vix_low REAL, vix_close REAL,
    pcr_close REAL,
    max_oi_strike_ce INTEGER, max_oi_strike_pe INTEGER,
    max_oi_ce INTEGER, max_oi_pe INTEGER,
    total_ce_oi INTEGER, total_pe_oi INTEGER,
    n_minutes_captured INTEGER, n_minutes_expected INTEGER
);
CREATE UNIQUE INDEX IF NOT EXISTS idx_ds ON daily_summary(date, underlying);

CREATE TABLE IF NOT EXISTS holidays (
    date TEXT PRIMARY KEY,
    description TEXT
);

CREATE TABLE IF NOT EXISTS premarket_cues (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    ts_utc INTEGER NOT NULL,
    ist_minute TEXT NOT NULL,
    fetch_slot TEXT NOT NULL,           -- e.g. '08:45'
    source TEXT NOT NULL,               -- yfinance
    cues_json TEXT NOT NULL             -- {S&P500, NASDAQ, ...}
);
CREATE INDEX IF NOT EXISTS idx_pc_ts ON premarket_cues(ts_utc);

CREATE TABLE IF NOT EXISTS constituents (
    date TEXT NOT NULL,
    ticker TEXT NOT NULL,
    index_tag TEXT NOT NULL,            -- NIFTY50 / NIFTYBANK
    has_fo INTEGER NOT NULL,            -- 1 / 0
    PRIMARY KEY (date, ticker, index_tag)
);
CREATE INDEX IF NOT EXISTS idx_const_date ON constituents(date);

CREATE TABLE IF NOT EXISTS candles_runs (
    date TEXT PRIMARY KEY,              -- the backfilled trading day
    n_symbols INTEGER,
    n_candles INTEGER,
    n_failed INTEGER,                   -- 0 => that day is done; skip it nightly
    legs_ok INTEGER,                    -- 1/0/NULL derivative-probe verdict
    completed_at TEXT                   -- ISO timestamp of the recorded run
);
""";

    /// <summary>
    /// Split a SQL script into executable statements. Handles single-quoted
    /// strings (with '' escapes) and <c>--</c> line comments, so the inline
    /// comments carried over from the Python DDL never break a split.
    /// </summary>
    public static IReadOnlyList<string> SplitStatements(string script)
    {
        var statements = new List<string>();
        var current = new StringBuilder(script.Length);
        var n = script.Length;

        for (var i = 0; i < n; i++)
        {
            var ch = script[i];

            if (ch == '\'')
            {
                current.Append(ch);
                i++;
                while (i < n && script[i] != '\'')
                {
                    current.Append(script[i]);
                    i++;
                }
                if (i < n)
                    current.Append('\'');   // closing quote
                continue;
            }

            if (ch == '-' && i + 1 < n && script[i + 1] == '-')
            {
                // line comment: copy through to end of line. The newline that
                // ENDS the comment must be kept in the output, or the comment
                // swallows the next line (and its columns) in SQLite too.
                while (i < n && script[i] != '\n')
                {
                    current.Append(script[i]);
                    i++;
                }
                if (i < n)
                    current.Append('\n');
                continue;
            }

            if (ch == ';')
            {
                Flush(current, statements);
                continue;
            }

            current.Append(ch);
        }

        Flush(current, statements);
        return statements;
    }

    private static void Flush(StringBuilder current, List<string> statements)
    {
        var text = current.ToString().Trim();
        current.Clear();
        if (text.Length > 0)
            statements.Add(text);
    }
}
