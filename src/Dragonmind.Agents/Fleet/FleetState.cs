namespace Dragonmind.Agents.Fleet;

/// <summary>
/// How a service is currently behaving. Reported by the environment, never inferred by an agent.
/// </summary>
public enum ServiceHealth
{
    /// <summary>Serving normally.</summary>
    Healthy,

    /// <summary>Serving, but outside its normal operating envelope.</summary>
    Degraded,

    /// <summary>Not serving.</summary>
    Down
}

/// <summary>
/// One service in the fleet.
/// </summary>
/// <param name="Name">Stable identifier, and the entity name used when facts about this service
/// are written to or read from the knowledge graph.</param>
/// <param name="Version">The version currently deployed.</param>
/// <param name="Health">Current health.</param>
/// <remarks>
/// Note what is not here: dependencies. Which services call which lives in the knowledge graph and
/// nowhere else. Keeping a copy on the record as well would be two answers to one question, and the
/// two would drift the first time one was updated without the other - which is exactly the kind of
/// disagreement a Correction turn exists to resolve, so the steward had better not create it.
/// </remarks>
public sealed record ServiceRecord(
    string Name,
    string Version,
    ServiceHealth Health);

/// <summary>
/// One completed exchange, kept so the steward can answer questions that refer back to it
/// ("undo what you just did", "why did you refuse that").
/// </summary>
/// <param name="Request">What the operator said.</param>
/// <param name="Response">What the steward replied.</param>
public sealed record TurnRecord(string Request, string Response);

/// <summary>
/// A single change to <see cref="FleetState"/>, produced by a handler and applied through
/// <c>IWorkingState</c> rather than by the handler mutating state directly.
/// </summary>
/// <remarks>
/// Handlers return changes instead of applying them so that the set of mutations a turn made is a
/// value the orchestrator (and a test) can inspect. A handler that mutated state in place would
/// leave nothing to assert on beyond the final state, which cannot distinguish "did nothing"
/// from "did two things that cancelled out".
/// </remarks>
public abstract record StateChange
{
    private StateChange()
    {
    }

    /// <summary>A service's deployed version changed.</summary>
    public sealed record VersionChanged(string Service, string Version) : StateChange;

    /// <summary>A service's reported health changed.</summary>
    public sealed record HealthChanged(string Service, ServiceHealth Health) : StateChange;

    /// <summary>The change window opened or closed.</summary>
    public sealed record ChangeWindowSet(bool Open) : StateChange;

}

/// <summary>
/// Everything the steward knows about the fleet for one scope. Immutable: a turn produces a new
/// instance rather than editing this one, so the state a handler was given cannot change underneath
/// it while the turn is still running.
/// </summary>
/// <param name="Services">The fleet.</param>
/// <param name="ChangeWindowOpen">Whether changes are currently permitted. This single flag is the
/// only piece of state the routing policy reads, which is what keeps the policy finitely
/// enumerable — see <c>RoutePolicy</c>.</param>
/// <param name="RecentTurns">Most recent exchanges, oldest first.</param>
public sealed record FleetState(
    IReadOnlyList<ServiceRecord> Services,
    bool ChangeWindowOpen,
    IReadOnlyList<TurnRecord> RecentTurns)
{
    /// <summary>
    /// Returns the service with this name, or <see langword="null"/>. Comparison is ordinal and
    /// case-insensitive: service names arrive from model output and from operators typing them by
    /// hand, so casing varies, but the comparison must not vary with the host's culture.
    /// </summary>
    public ServiceRecord? Find(string name) =>
        Services.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Returns a new state with <paramref name="change"/> applied. A change naming a service that
    /// does not exist is returned unapplied rather than creating one: the steward reports the
    /// fleet, it does not invent it.
    /// </summary>
    public FleetState Apply(StateChange change) => change switch
    {
        StateChange.ChangeWindowSet c => this with { ChangeWindowOpen = c.Open },

        StateChange.VersionChanged c when Find(c.Service) is not null =>
            this with { Services = Replace(c.Service, s => s with { Version = c.Version }) },

        StateChange.HealthChanged c when Find(c.Service) is not null =>
            this with { Services = Replace(c.Service, s => s with { Health = c.Health }) },

        _ => this
    };

    private IReadOnlyList<ServiceRecord> Replace(string name, Func<ServiceRecord, ServiceRecord> update) =>
        [.. Services.Select(s =>
            string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase) ? update(s) : s)];
}
