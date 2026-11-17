using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Podish.PerfTools;

internal static partial class Program
{
    internal sealed record TrainingText(string Text, string FixedPattern, string RegexPattern, string SedArguments,
        string SedExpected);

    internal static TrainingText CreateTrainingText(string corpus, uint seed, int bytes)
    {
        if (seed == 0 || bytes < 1) throw new ArgumentOutOfRangeException(nameof(seed));
        var text = new StringBuilder(bytes + 2048);
        uint random = seed;
        uint Next()
        {
            random ^= random << 13; random ^= random >> 17; random ^= random << 5;
            return random;
        }
        for (var line = 0; text.Length < bytes; line++)
        {
            var value = Next();
            var error = line % 7 == 0;
            var needle = line % 11 == 0 ? "needle" : "other";
            switch (corpus)
            {
                case "records-v1":
                    text.Append(CultureInfo.InvariantCulture, $"item={line:D8} status={(error ? "ERROR" : "OK")} tag={needle} value={value:X8} payload=abcdefghijklmnopqrstuvwxyz0123456789\n");
                    break;
                case "logs-v2":
                    text.Append(CultureInfo.InvariantCulture, $"2026-11-{line % 28 + 1:D2}T{line % 24:D2}:{line % 60:D2}:00Z host=node{line % 17} level={(error ? "ERROR" : "INFO")} status={(error ? 503 : 200)} path=/api/v1/items/{line % 4096} request={value:X8} latency={value % 10000} message={needle}");
                    for (var j = 0; j < (line % 13 == 0 ? 48 : line % 5); j++)
                        text.Append(CultureInfo.InvariantCulture, $" field{j}={Next():X8}");
                    text.Append('\n');
                    break;
                case "source-v2":
                    text.Append(CultureInfo.InvariantCulture, $"static int item_{line:D8}(int value) {{ return value + {value % 65536}; }} // {(error ? "ERROR" : "OK")} {needle}\n");
                    if (line % 19 == 0) text.Append("/* reusable buffer, bounds check, deterministic fixture */\n");
                    break;
                case "entropy-v2":
                    text.Append(error ? "ERROR token12345678 " : "INFO token87654321 ");
                    const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_+.,:/=-";
                    for (var j = 0; j < 512 + line % 513; j++) text.Append(alphabet[(int)(Next() % alphabet.Length)]);
                    text.Append(' ').Append(needle).Append('\n');
                    break;
                default: throw new ArgumentException("Unknown corpus: " + corpus);
            }
        }
        var valueText = text.ToString();
        return corpus switch
        {
            "records-v1" => new(valueText, "needle", "^item=[0-9]+ status=ERROR ",
                "-e 's/status=ERROR/status=WARN/g' -e 's/needle/token/g'",
                valueText.Replace("status=ERROR", "status=WARN", StringComparison.Ordinal).Replace("needle", "token", StringComparison.Ordinal)),
            "logs-v2" => new(valueText, "needle", "level=ERROR.*status=(500|503) ",
                "-e 's/level=ERROR/level=WARN/g' -e 's/request=[0-9A-F]*/request=REDACTED/g'",
                Regex.Replace(valueText.Replace("level=ERROR", "level=WARN", StringComparison.Ordinal), "request=[0-9A-F]*", "request=REDACTED")),
            "source-v2" => new(valueText, "needle", "^static int item_[0-9]+.*return value [+] [0-9]+;",
                "-e 's/static int/static long/g' -e 's/return value/return (long)value/g'",
                valueText.Replace("static int", "static long", StringComparison.Ordinal).Replace("return value", "return (long)value", StringComparison.Ordinal)),
            "entropy-v2" => new(valueText, "needle", "^ERROR [A-Za-z0-9]+ ",
                "-e 's/ERROR/WARNING/g' -e 's/needle/token/g'",
                valueText.Replace("ERROR", "WARNING", StringComparison.Ordinal).Replace("needle", "token", StringComparison.Ordinal)),
            _ => throw new ArgumentException("Unknown corpus")
        };
    }
}
