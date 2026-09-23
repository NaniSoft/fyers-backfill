using System.Globalization;
using System.Text.Json;

namespace Fyers.Backfill.Instruments;

/// <summary>
/// Streaming reader for the public Fyers symbol masters
/// (<c>https://public.fyers.in/sym_details/NSE_CM_sym_master.json</c>,
/// <c>NSE_FO_sym_master.json</c>, ...). The FO master is ~77 MB / ~74k entries, so
/// the DOM is never built: a single <see cref="Utf8JsonReader"/> pass keeps only
/// the seven fields an <see cref="Instrument"/> needs and discards the rest.
/// </summary>
public static class MasterScanner
{
    /// <summary>
    /// Every cash equity (<c>exSeries == "EQ"</c>), future (<c>optType == "XX"</c>
    /// with an expiry) and option (<c>optType CE/PE</c>) in a master file, in file
    /// order. Indices and non-tradable entries are skipped.
    /// </summary>
    public static List<Instrument> Scan(string path, string segment)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"symbol master not found: {path}", path);

        var bytes = File.ReadAllBytes(path);
        var reader = new Utf8JsonReader(bytes, isFinalBlock: true, new JsonReaderState());
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            throw new InvalidDataException($"symbol master {path}: expected a top-level JSON object");

        var result = new List<Instrument>();
        var entry = new Fields();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                break;
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new InvalidDataException($"symbol master {path}: malformed entry key");

            var key = reader.GetString() ?? "";
            reader.Read();                       // -> entry value
            ReadEntry(ref reader, ref entry);

            var instrument = ToInstrument(entry, key, segment);
            if (instrument is not null)
                result.Add(instrument);
        }
        return result;
    }

    private static Instrument? ToInstrument(in Fields f, string key, string segment)
    {
        var symbol = string.IsNullOrWhiteSpace(f.SymTicker) ? key : f.SymTicker!;

        // Cash equity: the -EQ series is the only one we backfill as "a stock".
        if (string.Equals(f.ExSeries, "EQ", StringComparison.OrdinalIgnoreCase))
            return new Instrument(symbol, Instrument.KindEquity, Isin: f.Isin, Segment: segment);

        var optType = f.OptType?.Trim().ToUpperInvariant();
        if (optType is "CE" or "PE")
        {
            return new Instrument(
                symbol,
                Instrument.KindOption,
                Underlying: f.UnderSym,
                ExpiryEpoch: f.Expiry,
                Strike: f.Strike,
                OptionType: optType,
                Segment: segment);
        }

        if (optType == "XX" && f.Expiry is > 0)
        {
            return new Instrument(
                symbol,
                Instrument.KindFuture,
                Underlying: f.UnderSym,
                ExpiryEpoch: f.Expiry,
                Segment: segment);
        }

        return null;
    }

    private static void ReadEntry(ref Utf8JsonReader reader, ref Fields f)
    {
        f.Reset();
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();
            return;
        }

        var depth = 0;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                if (depth == 0)
                    return;
                depth--;
                continue;
            }
            if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
            {
                depth++;
                continue;
            }
            if (reader.TokenType != JsonTokenType.PropertyName)
                continue;

            var name = reader.GetString();
            reader.Read();                   // -> property value
            switch (name)
            {
                case "symTicker":
                    f.SymTicker = StringOrNull(ref reader);
                    break;
                case "exSeries":
                    f.ExSeries = StringOrNull(ref reader);
                    break;
                case "isin":
                    f.Isin = StringOrNull(ref reader);
                    break;
                case "optType":
                    f.OptType = StringOrNull(ref reader);
                    break;
                case "underSym":
                    f.UnderSym = StringOrNull(ref reader);
                    break;
                case "expiryDate":
                    f.Expiry = Epoch(ref reader);
                    break;
                case "strikePrice":
                    f.Strike = DecimalOrNull(ref reader);
                    break;
                default:
                    if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                        depth++;
                    break;
            }
        }
    }

    private static string? StringOrNull(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.String)
            return reader.GetString();
        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
            reader.Skip();
        return null;
    }

    /// <summary>expiryDate is an epoch-seconds STRING ("1790676600"); tolerate numbers.</summary>
    private static long? Epoch(ref Utf8JsonReader reader)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return long.TryParse(reader.GetString(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var s) && s > 0 ? s : null;
            case JsonTokenType.Number:
                return reader.TryGetInt64(out var n) && n > 0 ? n : null;
            default:
                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                    reader.Skip();
                return null;
        }
    }

    private static decimal? DecimalOrNull(ref Utf8JsonReader reader)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                return reader.TryGetDecimal(out var d) ? d : null;
            case JsonTokenType.String:
                return decimal.TryParse(reader.GetString(), NumberStyles.Number,
                    CultureInfo.InvariantCulture, out var s) ? s : null;
            default:
                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                    reader.Skip();
                return null;
        }
    }

    private struct Fields
    {
        public string? SymTicker;
        public string? ExSeries;
        public string? Isin;
        public string? OptType;
        public string? UnderSym;
        public long? Expiry;
        public decimal? Strike;

        public void Reset()
        {
            SymTicker = null;
            ExSeries = null;
            Isin = null;
            OptType = null;
            UnderSym = null;
            Expiry = null;
            Strike = null;
        }
    }
}
