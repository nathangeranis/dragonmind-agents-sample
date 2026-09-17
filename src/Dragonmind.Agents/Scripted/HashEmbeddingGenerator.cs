using System.Security.Cryptography;

using Dragonmind.Core.AI;

namespace Dragonmind.Agents.Scripted;

/// <summary>
/// A deterministic <see cref="IEmbeddingGenerator"/> that derives a vector from a hash of the text,
/// so the knowledge layer can be exercised against a real database with no embedding API.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is not semantic search and must not be read as a stand-in for it.</b> Two passages about
/// the same subject in different words get unrelated vectors. What it does give, exactly, is that
/// the same text always yields the same vector — which turns out to be a stronger basis for an
/// integration test than a real model would be. Storing a passage and then searching for that same
/// text produces a cosine similarity of 1 and a deterministic first place, every run, so the test
/// asserts an identity round trip through pgvector rather than a ranking that could drift with the
/// model.
/// </para>
/// <para>
/// It makes the same three guarantees the production generator does, because the vector store cares
/// about all three: exactly <see cref="EmbeddingDimensions.Default"/> components, every one finite,
/// and never an all-zero vector. The last matters most — pgvector's cosine distance operator errors
/// on a zero-norm vector, so a generator that could emit one turns a retrieval into a database
/// error. Registering this in place of the real generator also bypasses the width enforcement the
/// real one performs, which is why the width is asserted here rather than assumed.
/// </para>
/// </remarks>
public sealed class HashEmbeddingGenerator : IEmbeddingGenerator
{
    /// <summary>The width every vector must have, taken from the knowledge layer rather than repeated.</summary>
    public static int Dimensions => EmbeddingDimensions.Default;

    /// <inheritdoc />
    public Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(text);

        return Task.FromResult(Generate(text));
    }

    /// <summary>
    /// Derives the vector. Exposed so a test can assert the three guarantees directly rather than
    /// only observing them through a database.
    /// </summary>
    public static float[] Generate(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var vector = new float[Dimensions];

        // Counter-mode over SHA-256: hash the text with a block index appended, and read floats out
        // of the digest, until the vector is full. Deterministic, dependency-free, and spread evenly
        // enough that the norm is never degenerate for any real input.
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        var block = 0;
        var written = 0;

        while (written < Dimensions)
        {
            var seed = new byte[bytes.Length + sizeof(int)];
            bytes.CopyTo(seed, 0);
            BitConverter.GetBytes(block).CopyTo(seed, bytes.Length);

            var digest = SHA256.HashData(seed);

            for (var i = 0; i + sizeof(uint) <= digest.Length && written < Dimensions; i += sizeof(uint))
            {
                // Map to [-1, 1] - closed at both ends, since raw == uint.MaxValue yields exactly
                // 1. Nothing here depends on the interval being half-open; what matters is that the
                // arithmetic is exact, so no component can be NaN or infinite.
                var raw = BitConverter.ToUInt32(digest, i);
                vector[written++] = (raw / (float)uint.MaxValue * 2f) - 1f;
            }

            block++;
        }

        Normalize(vector);

        return vector;
    }

    /// <summary>
    /// L2-normalizes in place, and refuses to produce a degenerate vector.
    /// </summary>
    private static void Normalize(float[] vector)
    {
        double sumOfSquares = 0;

        foreach (var component in vector)
        {
            sumOfSquares += (double)component * component;
        }

        var norm = Math.Sqrt(sumOfSquares);

        // A zero or non-finite norm cannot be normalized, and pgvector's cosine operator raises an
        // error on a zero-norm vector rather than returning a distance. Reaching this would mean the
        // derivation above was broken, so it throws here instead of writing something the database
        // will reject later with a message about a column rather than about this.
        if (norm == 0d || double.IsNaN(norm) || double.IsInfinity(norm))
        {
            throw new InvalidOperationException(
                "the derived embedding has a zero or non-finite norm, which cannot be normalized and " +
                "cannot be stored - this is a defect in the derivation, not a property of the input");
        }

        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] = (float)(vector[i] / norm);
        }
    }
}
