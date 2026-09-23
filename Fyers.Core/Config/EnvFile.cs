namespace Fyers.Core.Config;

/// <summary>
/// Parser for the .env FILE — port of <c>src/config.py:load_env()</c>.
///
/// PARITY RULE (hard): secrets come from this file, NEVER from the process
/// environment. config.py builds its env dict purely from the file's lines and
/// never consults <c>os.environ</c>, so setting FYERS_APP_ID in the shell must
/// have no effect here either.
///
/// Line rules (mirroring the Python):
///  - trim the whole line; skip blanks, <c>#</c> comments, and lines with no <c>=</c>;
///  - split on the FIRST <c>=</c> only (values may contain <c>=</c>);
///  - trim key and value;
///  - later duplicates win (the Python overwrites the dict entry).
///
/// One deliberate improvement over the Python: matching surrounding single or
/// double quotes are stripped from the value (<c>FYERS_PIN="123456"</c> yields
/// <c>123456</c>). config.py keeps the quote characters, which would corrupt the
/// TOTP/PIN secrets; nothing in this repo's .env legitimately wants them.
/// </summary>
public sealed class EnvFile
{
    private readonly Dictionary<string, string> _values;

    internal static readonly EnvFile Empty = new(new Dictionary<string, string>());

    private EnvFile(Dictionary<string, string> values) => _values = values;

    /// <summary>
    /// Reads the file at <paramref name="path"/>. A missing file yields an
    /// empty instance (the Python's <c>if os.path.exists(path)</c> guard), it
    /// does not throw.
    /// </summary>
    public static EnvFile Load(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        if (File.Exists(path))
        {
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                var eq = line.IndexOf('=');
                if (eq < 0)
                {
                    continue;
                }

                var key = line[..eq].Trim();
                if (key.Length == 0)
                {
                    continue;
                }

                values[key] = Unquote(line[(eq + 1)..].Trim());
            }
        }

        return new EnvFile(values);
    }

    /// <summary>The value for <paramref name="key"/>, or null when absent.</summary>
    public string? Get(string key) =>
        _values.TryGetValue(key, out var value) ? value : null;

    /// <summary>Same as <see cref="Get"/>: null when the key is absent.</summary>
    public string? this[string key] => Get(key);

    private static string Unquote(string value)
    {
        if (value.Length < 2)
        {
            return value;
        }

        var first = value[0];
        var last = value[^1];
        return first == last && first is '"' or '\''
            ? value[1..^1]
            : value;
    }
}
