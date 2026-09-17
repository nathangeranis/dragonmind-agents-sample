using Dragonmind.Agents.Classification;
using Dragonmind.Agents.Fleet;
using Dragonmind.Agents.Handlers;
using Dragonmind.Agents.Knowledge;
using Dragonmind.Agents.Routing;

namespace Dragonmind.Agents.Adapters;

/// <summary>
/// Builds each handler's input from the classified intent, the fleet, the route, and whatever the
/// adapter decides to retrieve on that handler's behalf.
/// </summary>
/// <remarks>
/// <para>
/// One adapter per handler, each producing only that handler's own request type. The production
/// orchestrator this pattern is drawn from does not do this — it passes the classification result
/// plus one shared, mutable context object to whichever handler runs. The README's design note
/// "Why every agent boundary is an adapter" sets out what that costs and what this buys; it is
/// kept in one place so the two cannot drift apart.
/// </para>
/// <para>
/// The short version, because it is what the code below is shaped by: a handler's request type is
/// the complete statement of what that handler depends on, retrieval belongs to the handler that
/// needs it rather than to a shared assembly step, and what a turn did stays a value rather than a
/// difference between two states.
/// </para>
/// </remarks>
public static class RequestAdapters
{
    /// <summary>How many passages to retrieve for a handler that wants context.</summary>
    private const int RecallLimit = 3;

    /// <summary>How far to traverse the graph around a corrected subject.</summary>
    private const int FactDepth = 2;

    /// <summary>
    /// Builds the explainer's input. Reached from two different intents: an ordinary question, and
    /// an action the routing policy refused.
    /// </summary>
    public static async Task<ExplainRequest> ForExplainerAsync(
        IntentResult intent,
        FleetState state,
        Route route,
        IScopedKnowledge knowledge,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(knowledge);

        var question = intent switch
        {
            IntentResult.StateQueried q => q.Question,

            // On the refusal route the operator asked for an action, not a question. Their request
            // is restated here so the explainer can name what it is declining; without it the reply
            // would be a reason attached to nothing.
            IntentResult.ActionRequested a => string.Join("; ", a.Actions.Select(x => $"{x.Verb} {x.Target}")),

            _ => throw new ArgumentOutOfRangeException(nameof(intent), intent, "not an explainer intent")
        };

        var recalled = await knowledge.RecallAsync(question, RecallLimit, cancellationToken).ConfigureAwait(false);

        // Which services the question is about decides what to traverse. Doing it here rather than
        // inside the handler is the adapter earning its place: the explainer gets facts about the
        // things that were mentioned, and nothing else, without ever holding a graph query itself.
        //
        // Matched as whole tokens rather than as substrings. A bare Contains would make a service
        // called "db" match any question mentioning "payments-db", pulling in unrelated facts - and
        // the question is normalised by a model, so names arrive surrounded by ordinary punctuation
        // rather than on their own.
        var words = Tokenize(question);

        var mentioned = state.Services
            .Where(s => words.Contains(s.Name))
            .Select(s => s.Name)
            .ToList();

        var facts = new List<string>();

        foreach (var name in mentioned)
        {
            var related = await knowledge.RelatedFactsAsync(name, FactDepth, cancellationToken).ConfigureAwait(false);
            facts.AddRange(related.Select(f => $"{f.Subject} {f.Predicate} {f.Object}"));
        }

        return new ExplainRequest(
            question,
            route.RefusalReason,
            Render(state),
            [.. recalled.Select(s => s.Content)],
            [.. facts.Distinct(StringComparer.OrdinalIgnoreCase)]);
    }

    /// <summary>
    /// Builds the policy handler's input. Retrieval is scoped to the services being changed, not to
    /// the operator's sentence: what matters here is what is known about the targets.
    /// </summary>
    public static async Task<PolicyRequest> ForPolicyAsync(
        IntentResult.ActionRequested intent,
        FleetState state,
        IScopedKnowledge knowledge,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(knowledge);

        var targets = string.Join(' ', intent.Actions.Select(a => a.Target).Distinct(StringComparer.OrdinalIgnoreCase));
        var recalled = await knowledge.RecallAsync(targets, RecallLimit, cancellationToken).ConfigureAwait(false);

        return new PolicyRequest(intent.Actions, Render(state), [.. recalled.Select(s => s.Content)]);
    }

    /// <summary>
    /// Builds the state handler's correction input, consulting the graph first.
    /// </summary>
    /// <remarks>
    /// The lookup happens here, before the handler exists, and its result is a required field of the
    /// request. That ordering is the safety property: a correction cannot be written without the
    /// existing facts having been fetched, because the handler cannot be called without them.
    /// A handler that fetched them itself could be edited later to skip the fetch, and nothing in
    /// the type system would notice.
    /// </remarks>
    public static async Task<StateRequest.ApplyCorrection> ForCorrectionAsync(
        IntentResult.CorrectionOffered intent,
        IScopedKnowledge knowledge,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(knowledge);

        var facts = await knowledge
            .RelatedFactsAsync(intent.Subject, FactDepth, cancellationToken)
            .ConfigureAwait(false);

        return new StateRequest.ApplyCorrection(
            intent.Subject,
            intent.Claim,
            intent.Correction,
            [.. facts.Select(f => $"{f.Subject} {f.Predicate} {f.Object}")]);
    }

    /// <summary>
    /// Builds the state handler's checkpoint input. Retrieves nothing: a checkpoint records what is
    /// true now, and recalling what was said about it earlier would only invite the model to blend
    /// the two.
    /// </summary>
    public static StateRequest.RecordCheckpoint ForCheckpoint(IntentResult.CheckpointRequested intent, FleetState state)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(state);

        return new StateRequest.RecordCheckpoint(intent.Label, Render(state));
    }

    /// <summary>
    /// Splits text into comparable words. Hyphens are kept, because a service name contains them;
    /// everything else that can sit against a name is a separator.
    /// </summary>
    private static HashSet<string> Tokenize(string text) =>
        new(text.Split(
                [' ', '\t', '\n', '\r', ',', '.', ';', ':', '?', '!', '(', ')', '[', ']', '"', '\'', '/'],
                StringSplitOptions.RemoveEmptyEntries),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Renders the fleet for a prompt. Handlers receive this, never <see cref="FleetState"/> itself,
    /// so no handler can reach a field the adapter did not choose to show it.
    /// </summary>
    private static IReadOnlyList<string> Render(FleetState state) =>
        [.. state.Services.Select(s => $"{s.Name} {s.Version} is {s.Health.ToString().ToUpperInvariant()}")];
}
