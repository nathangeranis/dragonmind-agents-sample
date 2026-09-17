using Dragonmind.Agents.Classification;

namespace Dragonmind.Agents.Routing;

/// <summary>
/// The handlers a turn can be routed to. Exactly one handles any given turn.
/// </summary>
public enum HandlerId
{
    /// <summary>Answers questions, and delivers refusals.</summary>
    Explainer,

    /// <summary>Evaluates a requested change against policy.</summary>
    Policy,

    /// <summary>Reads and writes what the steward knows.</summary>
    State
}

/// <summary>
/// Whether changes to the fleet are currently permitted.
/// </summary>
/// <remarks>
/// Two values, not three. An earlier shape had an <c>Either</c> member so a rule could apply in
/// both states, which reads well and is a trap: as soon as one intent has both an <c>Either</c> rule
/// and a window-specific one, two rules match and the winner is decided by position in the table.
/// A rule set where order is load-bearing is not an enumerable policy, it is a switch statement
/// wearing a table's clothes. Rules that apply in both states are written twice instead — see
/// <see cref="RoutePolicy.InEitherWindow"/> — so every cell of the grid is a row somebody wrote.
/// </remarks>
public enum ChangeWindow
{
    /// <summary>Changes are permitted.</summary>
    Open,

    /// <summary>Changes are not permitted.</summary>
    Closed
}

/// <summary>
/// The state the routing policy is allowed to read.
/// </summary>
/// <remarks>
/// Deliberately one flag. The policy is exhaustively testable only because its input domain is
/// small and finite: four intents times two window states is eight cells, and a test enumerates all
/// eight. Every field added here multiplies that grid, so anything a handler can decide for itself
/// belongs in the handler, not in the routing input.
/// </remarks>
public readonly record struct RoutingState(bool ChangeWindowOpen)
{
    /// <summary>The window state, in the form the rule table matches on.</summary>
    public ChangeWindow Window => ChangeWindowOpen ? ChangeWindow.Open : ChangeWindow.Closed;

    /// <summary>Projects the routing input out of a fleet state.</summary>
    public static RoutingState From(Fleet.FleetState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new RoutingState(state.ChangeWindowOpen);
    }
}

/// <summary>
/// One row of the routing table: for this intent in this window state, this handler, optionally
/// carrying the reason the request is being refused rather than performed.
/// </summary>
/// <param name="Intent">The classified intent.</param>
/// <param name="Window">The window state this row applies to.</param>
/// <param name="Handler">The single handler that takes the turn.</param>
/// <param name="RefusalReason">
/// Set when the route exists to say no. It lives on the route rather than inside a handler because
/// refusing is a policy decision, and a handler that decided for itself whether to refuse would be
/// a second, undocumented copy of the policy.
/// </param>
public sealed record RouteRule(Intent Intent, ChangeWindow Window, HandlerId Handler, string? RefusalReason = null);

/// <summary>
/// The resolved destination for one turn.
/// </summary>
/// <param name="Handler">Who handles it.</param>
/// <param name="RefusalReason">Why it is being refused, or <see langword="null"/> if it is not.</param>
public sealed record Route(HandlerId Handler, string? RefusalReason)
{
    /// <summary>Whether this route exists to refuse the request.</summary>
    public bool IsRefusal => RefusalReason is not null;
}

/// <summary>
/// Maps a classified intent plus the current state to exactly one handler.
/// </summary>
public interface IRoutePolicy
{
    /// <summary>Every rule, in no significant order. Exposed so it can be enumerated and tested.</summary>
    IReadOnlyList<RouteRule> Rules { get; }

    /// <summary>
    /// Resolves the single handler for this turn.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The table does not contain exactly one rule for this combination.
    /// </exception>
    Route Resolve(Intent intent, RoutingState state);
}

/// <summary>
/// The routing policy: a total function from (intent, state) to one handler, written as data.
/// </summary>
/// <remarks>
/// <para>
/// The model classifies; this decides. Keeping the two apart is the whole point of the sample, and
/// it buys three things a model naming its own target cannot give you:
/// </para>
/// <list type="bullet">
/// <item>The mapping is enumerable, so a test can walk every reachable combination and prove each
/// resolves to exactly one handler. There is no equivalent of that for a sentence in a prompt.</item>
/// <item>The mapping can read state the model was never shown. The change window is not in the
/// classifier's projection at all, so "restart orders-api" classifies identically whether or not
/// changes are permitted, and the same text still routes two different ways.</item>
/// <item>A bad classification is contained. The worst a wrong intent can do is reach the wrong
/// handler with its own validated payload; it cannot reach a handler that was never a legal
/// destination for it.</item>
/// </list>
/// </remarks>
public sealed class RoutePolicy : IRoutePolicy
{
    /// <summary>The refusal an action request receives while the change window is closed.</summary>
    public const string ChangeWindowClosed =
        "the change window is closed, so no change to the fleet can be made right now";

    /// <inheritdoc />
    public IReadOnlyList<RouteRule> Rules { get; } =
    [
        // The one state-dependent route, and the reason the sample is built around this domain:
        // identical text goes to a different handler depending on state the classifier never saw.
        new RouteRule(Intent.Action, ChangeWindow.Open, HandlerId.Policy),
        new RouteRule(Intent.Action, ChangeWindow.Closed, HandlerId.Explainer, ChangeWindowClosed),

        // Questions never change anything, so the window is irrelevant to them.
        .. InEitherWindow(Intent.StateQuery, HandlerId.Explainer),

        // Corrections and checkpoints both change what the steward KNOWS, not what the fleet IS,
        // so they are not gated on the change window either. They share a handler and still get
        // their own adapter - two intents, one handler, two different inputs.
        .. InEitherWindow(Intent.Correction, HandlerId.State),
        .. InEitherWindow(Intent.Checkpoint, HandlerId.State)
    ];

    /// <summary>
    /// Writes the same routing decision for both window states.
    /// </summary>
    /// <remarks>
    /// A convenience for building the table, never a value that participates in matching. That
    /// distinction is what keeps "exactly one rule matches" true by construction: after expansion
    /// every row names one concrete window, so two rows can only collide by being genuine
    /// duplicates — which <see cref="Resolve"/> then throws on instead of silently preferring one.
    /// </remarks>
    public static IEnumerable<RouteRule> InEitherWindow(Intent intent, HandlerId handler, string? refusalReason = null)
    {
        yield return new RouteRule(intent, ChangeWindow.Open, handler, refusalReason);
        yield return new RouteRule(intent, ChangeWindow.Closed, handler, refusalReason);
    }

    /// <inheritdoc />
    public Route Resolve(Intent intent, RoutingState state)
    {
        // Single, not First. First would pick a winner by table position whenever a future edit
        // introduced an overlapping rule, and would return the wrong handler silently. Single turns
        // both an overlap and a gap into an immediate, loud failure at the moment the table is
        // wrong, rather than at the moment someone notices a turn went to the wrong place.
        var rule = Rules.Single(r => r.Intent == intent && r.Window == state.Window);

        return new Route(rule.Handler, rule.RefusalReason);
    }
}
