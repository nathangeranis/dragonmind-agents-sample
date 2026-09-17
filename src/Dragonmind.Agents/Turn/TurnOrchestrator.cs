using Dragonmind.Agents.Adapters;
using Dragonmind.Agents.Classification;
using Dragonmind.Agents.Fleet;
using Dragonmind.Agents.Handlers;
using Dragonmind.Agents.Knowledge;
using Dragonmind.Agents.Routing;

namespace Dragonmind.Agents.Turn;

/// <summary>
/// The state a turn reads and writes while it is running.
/// </summary>
/// <remarks>
/// A turn does not mutate the fleet directly. It reads <see cref="Current"/>, hands changes to
/// <see cref="Apply"/>, and the orchestrator commits the result at the end. The reason is that
/// "what this turn did" stays a value somebody can look at: with in-place mutation the only
/// evidence left is the difference between two states, which cannot tell a turn that did nothing
/// from one that did two things which cancelled out.
/// </remarks>
public interface IWorkingState
{
    /// <summary>The fleet as it stands, including changes applied so far in this turn.</summary>
    FleetState Current { get; }

    /// <summary>
    /// Applies a change. Returns <see langword="false"/> when it did not move the state — which
    /// happens when it names a service the fleet does not have — in which case it is NOT recorded.
    /// </summary>
    /// <remarks>
    /// Returning a result rather than applying blindly is what keeps <see cref="Changes"/> honest.
    /// A change that was recorded but had no effect is the worst of both worlds: the caller believes
    /// something happened, and the state disagrees, with nothing to reconcile the two.
    /// </remarks>
    bool Apply(StateChange change);

    /// <summary>Everything applied during this turn, in order.</summary>
    IReadOnlyList<StateChange> Changes { get; }
}

/// <summary>
/// The default <see cref="IWorkingState"/>: one instance per turn, starting from the committed state.
/// </summary>
public sealed class TurnWorkingState(FleetState initial) : IWorkingState
{
    private readonly List<StateChange> _changes = [];

    /// <inheritdoc />
    public FleetState Current { get; private set; } = initial ?? throw new ArgumentNullException(nameof(initial));

    /// <inheritdoc />
    public IReadOnlyList<StateChange> Changes => _changes;

    /// <inheritdoc />
    public bool Apply(StateChange change)
    {
        ArgumentNullException.ThrowIfNull(change);

        var next = Current.Apply(change);

        // FleetState.Apply returns `this` - the same reference - when a change names a service that
        // is not in the fleet. Reference equality is therefore an exact test for "nothing moved",
        // and is not a heuristic: every arm that does change something builds a new instance.
        if (ReferenceEquals(next, Current))
        {
            return false;
        }

        _changes.Add(change);
        Current = next;
        return true;
    }
}

/// <summary>
/// What one turn produced.
/// </summary>
/// <param name="Reply">The message for the operator.</param>
/// <param name="State">The fleet after the turn.</param>
/// <param name="Intent">The classified intent, or <see langword="null"/> if the turn was not classified.</param>
/// <param name="Handler">The handler that ran, or <see langword="null"/> if none did.</param>
/// <param name="Changes">The changes the turn made.</param>
public sealed record TurnOutcome(
    string Reply,
    FleetState State,
    Intent? Intent,
    HandlerId? Handler,
    IReadOnlyList<StateChange> Changes);

/// <summary>
/// Runs one turn: classify, route, adapt, hand to exactly one handler, commit.
/// </summary>
/// <remarks>
/// <para>
/// The whole pattern is the body of <see cref="RunAsync"/>, and it is meant to be readable in one
/// sitting. Each step is a named thing that can be tested on its own — the classifier without a
/// router, the router without a model, an adapter without a handler — which is most of the argument
/// for splitting them up in the first place.
/// </para>
/// <para>
/// The order matters and is the safety property: classification is validated before a route is
/// resolved, and a route is resolved before any adapter runs. An answer that does not satisfy its
/// own schema therefore ends the turn before a handler has been chosen, before anything is
/// retrieved, and before anything is written.
/// </para>
/// </remarks>
public sealed class TurnOrchestrator(
    IIntentAgent intentAgent,
    IRoutePolicy routePolicy,
    IExplainerAgent explainer,
    IPolicyAgent policy,
    IStateAgent state,
    IScopedKnowledge knowledge)
{
    private readonly IIntentAgent _intentAgent = intentAgent ?? throw new ArgumentNullException(nameof(intentAgent));
    private readonly IRoutePolicy _routePolicy = routePolicy ?? throw new ArgumentNullException(nameof(routePolicy));
    private readonly IExplainerAgent _explainer = explainer ?? throw new ArgumentNullException(nameof(explainer));
    private readonly IPolicyAgent _policy = policy ?? throw new ArgumentNullException(nameof(policy));
    private readonly IStateAgent _state = state ?? throw new ArgumentNullException(nameof(state));
    private readonly IScopedKnowledge _knowledge = knowledge ?? throw new ArgumentNullException(nameof(knowledge));

    /// <summary>
    /// Runs one turn against <paramref name="fleet"/> and returns the outcome. The input state is
    /// never modified; the new state is on the outcome.
    /// </summary>
    public async Task<TurnOutcome> RunAsync(
        Interaction interaction,
        FleetState fleet,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(interaction);
        ArgumentNullException.ThrowIfNull(fleet);

        // 1. The model classifies. It is shown a projection, not the state, and it is not shown the
        //    change window at all.
        var classification = await _intentAgent
            .ClassifyAsync(interaction, StateProjection.From(fleet), cancellationToken)
            .ConfigureAwait(false);

        if (classification is IntentClassification.Unclassified unclassified)
        {
            // No handler was selected, so nothing was retrieved and nothing was written. Asking
            // again is the whole recovery path; there is deliberately no default intent to fall
            // back to, because the cheapest wrong answer here is one that gets acted on.
            //
            // The attempt is still remembered. RecentRequests exists so the next turn can resolve a
            // back-reference - "try that again", "I meant the other one" - and the turn most likely
            // to be referred back to is the one that just failed. Every other outcome, including a
            // handler that failed to decode its own reply, is recorded; leaving only this one out
            // would make the classifier blind to precisely the turn the operator is rephrasing.
            var refusal = $"I could not tell what you were asking for - {unclassified.Reason}. Could you put it another way?";

            return new TurnOutcome(
                refusal,
                Remember(fleet, interaction.Text, refusal),
                Intent: null,
                Handler: null,
                Changes: []);
        }

        var intent = ((IntentClassification.Classified)classification).Result;

        // 2. Code routes. The policy reads state the classifier never saw, which is how the same
        //    sentence reaches two different handlers on two different days.
        var route = _routePolicy.Resolve(intent.Intent, RoutingState.From(fleet));

        var working = new TurnWorkingState(fleet);

        // 3. The adapter for that one handler builds that one handler's input, and runs whatever
        //    retrieval that handler needs - no more.
        // 4. Exactly one handler runs.
        var response = await DispatchAsync(intent, route, working, cancellationToken).ConfigureAwait(false);

        // 5. The changes the handler decided on are applied here, not by the handler - and a change
        //    that does not land is reported rather than swallowed. A handler can approve an action
        //    against a service the fleet does not have (nothing upstream checks the target against
        //    the fleet, and a model will not reliably notice a typo), and telling the operator it
        //    was done while the state is untouched is the failure this whole design is meant to
        //    make impossible.
        var unapplied = response.Changes.Where(change => !working.Apply(change)).ToList();

        var message = unapplied.Count == 0
            ? response.Message
            : $"{response.Message} {DescribeUnapplied(unapplied)}";

        return new TurnOutcome(
            message,
            Remember(working.Current, interaction.Text, message),
            intent.Intent,
            route.Handler,
            working.Changes);
    }

    private Task<TurnResponse> DispatchAsync(
        IntentResult intent,
        Route route,
        IWorkingState working,
        CancellationToken cancellationToken) => route.Handler switch
        {
            HandlerId.Explainer => ExplainAsync(intent, route, working, cancellationToken),
            HandlerId.Policy => EvaluateAsync(intent, working, cancellationToken),
            HandlerId.State => ApplyStateAsync(intent, working, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(route), route, "unhandled route")
        };

    private async Task<TurnResponse> ExplainAsync(
        IntentResult intent,
        Route route,
        IWorkingState working,
        CancellationToken cancellationToken)
    {
        var request = await RequestAdapters
            .ForExplainerAsync(intent, working.Current, route, _knowledge, cancellationToken)
            .ConfigureAwait(false);

        return await _explainer.ExplainAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TurnResponse> EvaluateAsync(
        IntentResult intent,
        IWorkingState working,
        CancellationToken cancellationToken)
    {
        // The cast is safe by construction rather than by luck: the routing table sends nothing but
        // Intent.Action to the policy handler, and Intent.Action binds only to ActionRequested. If
        // a future table edit broke that, this would throw here rather than quietly hand the
        // handler something it cannot use.
        var actions = (IntentResult.ActionRequested)intent;

        var request = await RequestAdapters
            .ForPolicyAsync(actions, working.Current, _knowledge, cancellationToken)
            .ConfigureAwait(false);

        return await _policy.EvaluateAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TurnResponse> ApplyStateAsync(
        IntentResult intent,
        IWorkingState working,
        CancellationToken cancellationToken)
    {
        // Two intents route here, and each gets its own adapter producing its own request shape.
        // The handler switches on the shape, so it never has to be told which intent it came from.
        StateRequest request = intent switch
        {
            IntentResult.CorrectionOffered correction => await RequestAdapters
                .ForCorrectionAsync(correction, _knowledge, cancellationToken)
                .ConfigureAwait(false),

            IntentResult.CheckpointRequested checkpoint =>
                RequestAdapters.ForCheckpoint(checkpoint, working.Current),

            _ => throw new ArgumentOutOfRangeException(nameof(intent), intent, "not a state intent")
        };

        return await _state.ApplyAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Names the changes that could not be applied, so the reply contradicts itself out loud rather
    /// than quietly.
    /// </summary>
    private static string DescribeUnapplied(IReadOnlyList<StateChange> unapplied)
    {
        var names = unapplied
            .Select(change => change switch
            {
                StateChange.VersionChanged c => c.Service,
                StateChange.HealthChanged c => c.Service,
                _ => null
            })
            .Where(name => name is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return names.Count == 0
            ? "One change could not be applied, so nothing was altered."
            : $"I could not apply that: this fleet has no {string.Join(" or ", names)}. Nothing was changed.";
    }

    /// <summary>How many past turns to keep. Enough to resolve a reference, not a transcript.</summary>
    private const int HistoryLimit = 10;

    private static FleetState Remember(FleetState state, string request, string reply) =>
        state with { RecentTurns = [.. state.RecentTurns.Append(new TurnRecord(request, reply)).TakeLast(HistoryLimit)] };
}
