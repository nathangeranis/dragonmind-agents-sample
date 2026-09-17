using System.Text;
using System.Text.Json;

using ToonFormat;

namespace Dragonmind.Agents.Toon;

/// <summary>
/// Encodes and decodes TOON, the structured-output notation every agent in this sample reads and
/// writes. TOON is an open format; the decoder here is the <c>Toon.DotNet</c> package and this type
/// is only the thin layer around it that the agents share.
/// </summary>
/// <remarks>
/// <para>
/// Why TOON rather than JSON: the notation declares an array's length in its header
/// (<c>services[2]:</c>) and then writes the rows without repeating field names per row, so a list
/// of uniform objects costs markedly fewer tokens than the equivalent JSON. In a system where the
/// same response schema is sent on every turn, that is the cheapest structural saving available.
/// This repository makes no claim about the size of that saving: measuring it properly means
/// measuring real traffic, and a number quoted without that is decoration.
/// </para>
/// <para>
/// <b>Encoding and decoding must agree on property naming, and only one of them is configurable.</b>
/// <c>Toon.Encode</c> takes no serializer options — it always emits camelCase — while
/// <c>Toon.Decode</c> takes a <see cref="JsonSerializerOptions"/> and would happily be configured
/// to expect something else. Decoding with, say, a snake_case policy against a camelCase encoder
/// produces no error at all: unmatched properties simply keep their default values, so a list comes
/// back empty and a string comes back null with nothing logged. Single-word properties round-trip
/// either way, which is what makes the bug survive a casual test. <see cref="JsonOptions"/>
/// therefore pins camelCase to match the encoder, and a round-trip test over a deliberately
/// multi-word property pins that they stay in agreement.
/// </para>
/// </remarks>
public static class ToonCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private static readonly DecodeOptions Decoding = new() { Strict = false };

    /// <summary>
    /// Encodes a value as TOON, for embedding in a prompt.
    /// </summary>
    public static string Encode<T>(T value) => ToonFormat.Toon.Encode(value!);

    /// <summary>
    /// Decodes model output into <typeparamref name="T"/>, cleaning the wrapping a model commonly
    /// adds. Returns <see langword="false"/> rather than throwing: a malformed response is an
    /// expected outcome on this path, not an exceptional one, and the caller has a refusal to fall
    /// back to.
    /// </summary>
    public static bool TryDecode<T>(string? toon, out T? value)
    {
        value = default;

        if (string.IsNullOrWhiteSpace(toon))
        {
            return false;
        }

        try
        {
            value = ToonFormat.Toon.Decode<T>(Clean(toon), Decoding, JsonOptions);
            return value is not null;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or ArgumentException)
        {
            value = default;
            return false;
        }
    }

    /// <summary>
    /// Strips the two things a model reliably adds around structured output: a markdown code fence,
    /// and trailing <c>#</c> commentary explaining what it just produced.
    /// </summary>
    /// <remarks>
    /// The comment strip is quote-aware. A naive strip from the first <c>#</c> would cut
    /// <c>label: "release #4"</c> down to <c>label: "release</c>, turning a valid response into an
    /// unterminated string — a decode failure caused entirely by the cleaner.
    /// </remarks>
    internal static string Clean(string toon)
    {
        var text = toon.Replace("\r\n", "\n", StringComparison.Ordinal)
                       .Replace('\r', '\n');

        var lines = new List<string>();

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd();

            // A fence line is the fence and nothing else, with an optional language tag. Anything
            // else beginning with ``` is content and is left alone.
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                continue;
            }

            line = StripTrailingComment(line);

            if (line.Length > 0)
            {
                lines.Add(line);
            }
        }

        return string.Join('\n', lines).Trim();
    }

    private static string StripTrailingComment(string line)
    {
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (c == '\\' && inQuotes)
            {
                i++;
                continue;
            }

            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (c == '#' && !inQuotes)
            {
                return line[..i].TrimEnd();
            }
        }

        return line;
    }

    /// <summary>
    /// Renders a TOON array header and its rows, used when building the worked examples that teach
    /// each response schema. Kept here so examples in prompts are produced by the same notation
    /// rules the decoder enforces, rather than hand-typed and drifting.
    /// </summary>
    internal static string Array(string key, IReadOnlyList<string> rows)
    {
        var sb = new StringBuilder();
        sb.Append(key).Append('[').Append(rows.Count).Append("]:");

        foreach (var row in rows)
        {
            sb.Append('\n').Append("  ").Append(row);
        }

        return sb.ToString();
    }
}
