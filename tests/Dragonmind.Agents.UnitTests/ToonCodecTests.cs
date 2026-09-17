using Dragonmind.Agents.Toon;

namespace Dragonmind.Agents.UnitTests;

/// <summary>
/// Pins the notation layer every agent depends on.
/// </summary>
public class ToonCodecTests
{
    private sealed record Probe(string Label, IReadOnlyList<string> ServiceNames, int RetryBudget);

    [Fact]
    public void EncodeThenDecode_RoundTripsAMultiWordProperty()
    {
        // The guard that matters. Toon.Encode takes no serializer options and always emits
        // camelCase; Toon.Decode takes options and could be configured to expect anything. If the
        // two disagree, a mismatched property does not throw - it silently keeps its default, so a
        // list comes back empty and a number comes back zero with nothing logged.
        //
        // Single-word properties round-trip under almost any naming policy, which is exactly what
        // lets the bug survive a casual test. Every property here is deliberately multi-word.
        var original = new Probe("before the rollout", ["orders-api", "payments-db"], 3);

        var encoded = ToonCodec.Encode(original);
        Assert.True(ToonCodec.TryDecode<Probe>(encoded, out var decoded));

        Assert.NotNull(decoded);
        Assert.Equal(original.Label, decoded.Label);
        Assert.Equal(original.RetryBudget, decoded.RetryBudget);
        Assert.Equal(original.ServiceNames, decoded.ServiceNames);
    }

    [Fact]
    public void TryDecode_StripsAMarkdownCodeFence()
    {
        const string fenced = """
            ```toon
            label: fenced
            serviceNames[1]: orders-api
            retryBudget: 1
            ```
            """;

        Assert.True(ToonCodec.TryDecode<Probe>(fenced, out var decoded));
        Assert.Equal("fenced", decoded!.Label);
    }

    [Fact]
    public void Clean_StripsATrailingComment()
    {
        Assert.Equal("label: x", ToonCodec.Clean("label: x   # the model explaining itself"));
    }

    [Fact]
    public void Clean_LeavesAHashInsideAQuotedString()
    {
        // A naive strip from the first '#' turns this into an unterminated string, so the cleaner
        // would be the sole cause of the decode failure it is supposed to prevent.
        const string line = """label: "release #4" """;

        Assert.Equal("""label: "release #4" """.TrimEnd(), ToonCodec.Clean(line));
    }

    [Fact]
    public void TryDecode_ReturnsFalseForGarbage()
    {
        Assert.False(ToonCodec.TryDecode<Probe>("this is not structured output at all {{{", out var decoded));
        Assert.Null(decoded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryDecode_ReturnsFalseForEmptyInput(string? input)
    {
        Assert.False(ToonCodec.TryDecode<Probe>(input, out _));
    }
}
