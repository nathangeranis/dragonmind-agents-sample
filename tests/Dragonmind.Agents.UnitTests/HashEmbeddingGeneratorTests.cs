using Dragonmind.Agents.Scripted;

using Dragonmind.Core.AI;

namespace Dragonmind.Agents.UnitTests;

/// <summary>
/// Pins the three properties the vector store depends on. Registering this generator bypasses the
/// width enforcement the real one performs, so these are asserted rather than assumed.
/// </summary>
public class HashEmbeddingGeneratorTests
{
    public static TheoryData<string> Inputs() =>
    [
        "orders-api",
        "payments-db has been degraded since the 11.2 upgrade",
        "a",
        new string('x', 5000),
        "unicode: naïve café — ünïcödé",
        " ",
        string.Empty
    ];

    [Theory]
    [MemberData(nameof(Inputs))]
    public void EveryVectorHasExactlyTheConfiguredWidth(string input)
    {
        // Not a hard-coded 1536: the width is whatever the knowledge layer's column is, and taking
        // it from there means a change on that side fails here rather than at an INSERT.
        Assert.Equal(EmbeddingDimensions.Default, HashEmbeddingGenerator.Generate(input).Length);
    }

    [Theory]
    [MemberData(nameof(Inputs))]
    public void EveryComponentIsFinite(string input)
    {
        Assert.All(HashEmbeddingGenerator.Generate(input), c => Assert.True(float.IsFinite(c), $"non-finite component for '{input}'"));
    }

    [Theory]
    [MemberData(nameof(Inputs))]
    public void NoVectorIsDegenerate(string input)
    {
        // pgvector's cosine distance operator errors on a zero-norm vector, so an all-zero result
        // would surface as a database error during retrieval rather than as a bad embedding.
        var vector = HashEmbeddingGenerator.Generate(input);

        Assert.Contains(vector, c => c != 0f);

        var norm = Math.Sqrt(vector.Sum(c => (double)c * c));
        Assert.InRange(norm, 0.999, 1.001);
    }

    [Fact]
    public void TheSameTextAlwaysProducesTheSameVector()
    {
        // This is what lets the integration test assert an identity round trip: store a passage,
        // search for that same text, and the stored row scores 1 and ranks first, every run.
        const string text = "payments-db is degraded";

        Assert.Equal(HashEmbeddingGenerator.Generate(text), HashEmbeddingGenerator.Generate(text));
    }

    [Fact]
    public void DifferentTextProducesADifferentVector()
    {
        Assert.NotEqual(
            HashEmbeddingGenerator.Generate("orders-api"),
            HashEmbeddingGenerator.Generate("payments-db"));
    }

    [Fact]
    public async Task TheGeneratorSatisfiesTheKnowledgeLayerSeam()
    {
        IEmbeddingGenerator generator = new HashEmbeddingGenerator();

        var vector = await generator.GenerateEmbeddingAsync("orders-api");

        Assert.Equal(EmbeddingDimensions.Default, vector.Length);
    }
}
