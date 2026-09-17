using Dragonmind.Core.Application.AntiCorruptionLayer;
using Dragonmind.Core.Application.AntiCorruptionLayer.DTOs;
using Dragonmind.Core.Domain.SharedIdentities;

namespace Dragonmind.Agents.Knowledge;

/// <summary>
/// What a handler is allowed to do with the steward's memory.
/// </summary>
/// <remarks>
/// <para>
/// This is the same four operations the knowledge layer's facade offers, with the scope removed
/// from every signature because it is bound once at construction. A handler cannot pass the wrong
/// scope, cannot forget to pass one, and cannot reach another scope's memory — not by convention,
/// but because there is no parameter to get wrong.
/// </para>
/// <para>
/// <b>These are not model-invoked tools.</b> Nothing here is registered as a callable function and
/// no model decides when to invoke it; a handler calls these methods in ordinary C#, and whatever
/// comes back is folded into the prompt by that handler's adapter before the model sees it. The
/// retrieval decisions stay in code for the same reason the routing decisions do — they are
/// checkable, and they do not vary run to run.
/// </para>
/// </remarks>
public interface IScopedKnowledge
{
    /// <summary>Retrieves passages relevant to <paramref name="query"/> within this scope.</summary>
    Task<IReadOnlyList<KnowledgeSnippetDto>> RecallAsync(string query, int maxResults = 5, CancellationToken cancellationToken = default);

    /// <summary>Records a passage in this scope.</summary>
    Task RememberAsync(string content, string source, CancellationToken cancellationToken = default);

    /// <summary>Returns facts within <paramref name="maxDepth"/> hops of an entity, in this scope.</summary>
    Task<IReadOnlyList<KnowledgeFactDto>> RelatedFactsAsync(string entityName, int maxDepth = 2, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a subject-predicate-object fact in this scope. Returns <see langword="false"/> when
    /// the knowledge layer refuses the predicate, which it does for anything outside its own
    /// vocabulary. A caller must report that refusal rather than treat the write as done.
    /// </summary>
    Task<bool> RelateAsync(string subject, string predicate, string @object, CancellationToken cancellationToken = default);
}

/// <summary>
/// Binds one <see cref="ScopeId"/> to the knowledge facade, so handlers never handle a scope at all.
/// </summary>
public sealed class ScopedKnowledge(IKnowledgeContextFacade facade, ScopeId scope) : IScopedKnowledge
{
    private readonly IKnowledgeContextFacade _facade = facade ?? throw new ArgumentNullException(nameof(facade));
    private readonly ScopeId _scope = scope ?? throw new ArgumentNullException(nameof(scope));

    /// <inheritdoc />
    public Task<IReadOnlyList<KnowledgeSnippetDto>> RecallAsync(string query, int maxResults = 5, CancellationToken cancellationToken = default) =>
        _facade.SearchKnowledgeAsync(query, _scope, maxResults, cancellationToken);

    /// <inheritdoc />
    public async Task RememberAsync(string content, string source, CancellationToken cancellationToken = default) =>
        await _facade.StoreKnowledgeAsync(content, source, _scope, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public Task<IReadOnlyList<KnowledgeFactDto>> RelatedFactsAsync(string entityName, int maxDepth = 2, CancellationToken cancellationToken = default) =>
        _facade.GetRelatedFactsAsync(entityName, _scope, maxDepth, cancellationToken);

    /// <inheritdoc />
    public Task<bool> RelateAsync(string subject, string predicate, string @object, CancellationToken cancellationToken = default) =>
        _facade.AddKnowledgeFactAsync(subject, predicate, @object, _scope, cancellationToken: cancellationToken);
}
