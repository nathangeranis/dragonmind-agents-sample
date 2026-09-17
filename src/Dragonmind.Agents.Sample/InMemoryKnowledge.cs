using System.Collections.Concurrent;

using Dragonmind.Agents.Knowledge;

using Dragonmind.Core.Application.AntiCorruptionLayer.DTOs;

namespace Dragonmind.Agents.Sample;

/// <summary>
/// A memory that lives for the length of one run, so the sample works with nothing installed.
/// </summary>
/// <remarks>
/// <para>
/// Retrieval here is a case-insensitive word overlap. That is not a stand-in for semantic search
/// and is not pretending to be: it exists so the walkthrough has something to recall, and so the
/// shape of the calls a handler makes is visible without a database.
/// </para>
/// <para>
/// The real facade — pgvector for passages, Apache AGE for facts — implements the same
/// <see cref="IScopedKnowledge"/> port and drops in without a change to any agent. That swap is
/// exercised by <c>tests/Dragonmind.Agents.IntegrationTests</c>, which runs a whole turn against it;
/// this console host deliberately stays dependency-free so the walkthrough needs nothing installed.
/// </para>
/// <para>
/// It also keeps the relationship vocabulary honest. The real knowledge layer accepts a fixed set of
/// predicates and returns <see langword="false"/> for anything else, writing nothing; so does this,
/// using the same list, because a demo that accepted every predicate would hide the one branch the
/// state handler has for that case.
/// </para>
/// </remarks>
public sealed class InMemoryKnowledge : IScopedKnowledge
{
    /// <summary>
    /// The predicates the real knowledge layer accepts. Kept in step with it deliberately: this is
    /// the same list its own vocabulary holds, and the point of mirroring it is that the refusal
    /// path behaves the same way with and without a database.
    /// </summary>
    private static readonly HashSet<string> AllowedPredicates = new(StringComparer.OrdinalIgnoreCase)
    {
        "IS_A", "PART_OF", "LOCATED_IN", "DEPENDS_ON", "OWNS", "CREATED_BY",
        "MEMBER_OF", "RELATED_TO", "REFERENCES", "CONTAINS", "REPLACES", "KNOWS"
    };

    private readonly ConcurrentBag<(string Content, string Source)> _documents = [];
    private readonly ConcurrentBag<(string Subject, string Predicate, string Object)> _facts = [];

    /// <inheritdoc />
    public Task<IReadOnlyList<KnowledgeSnippetDto>> RecallAsync(string query, int maxResults = 5, CancellationToken cancellationToken = default)
    {
        var terms = Tokenise(query);

        IReadOnlyList<KnowledgeSnippetDto> hits =
        [
            .. _documents
                .Select(d => (Document: d, Score: Overlap(terms, Tokenise(d.Content))))
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .Take(maxResults)
                .Select(x => new KnowledgeSnippetDto
                {
                    DocumentId = Guid.Empty,
                    Content = x.Document.Content,
                    RelevanceScore = x.Score,
                    Source = x.Document.Source
                })
        ];

        return Task.FromResult(hits);
    }

    /// <inheritdoc />
    public Task RememberAsync(string content, string source, CancellationToken cancellationToken = default)
    {
        _documents.Add((content, source));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<KnowledgeFactDto>> RelatedFactsAsync(string entityName, int maxDepth = 2, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<KnowledgeFactDto> facts =
        [
            .. _facts
                .Where(f => string.Equals(f.Subject, entityName, StringComparison.OrdinalIgnoreCase)
                         || string.Equals(f.Object, entityName, StringComparison.OrdinalIgnoreCase))
                .Select(f => new KnowledgeFactDto
                {
                    Subject = f.Subject,
                    Predicate = f.Predicate,
                    Object = f.Object,
                    Distance = 1
                })
        ];

        return Task.FromResult(facts);
    }

    /// <inheritdoc />
    public Task<bool> RelateAsync(string subject, string predicate, string @object, CancellationToken cancellationToken = default)
    {
        // Normalised the way the real layer normalises: spaces to underscores, invariant uppercase.
        // Invariant rather than current-culture because a Turkish or Azeri host folds 'i' to a
        // dotted capital, which would stop a predicate matching its own entry in the list.
        var normalised = predicate.Trim().Replace(' ', '_').ToUpperInvariant();

        if (!AllowedPredicates.Contains(normalised))
        {
            return Task.FromResult(false);
        }

        _facts.Add((subject, normalised, @object));
        return Task.FromResult(true);
    }

    private static HashSet<string> Tokenise(string text) =>
        new(text.Split([' ', '\t', '\n', ',', '.', ';', ':', '?', '!', '"', '\''], StringSplitOptions.RemoveEmptyEntries),
            StringComparer.OrdinalIgnoreCase);

    private static double Overlap(HashSet<string> left, HashSet<string> right) =>
        left.Count == 0 ? 0 : (double)left.Count(right.Contains) / left.Count;
}
