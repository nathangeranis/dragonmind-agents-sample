using Dragonmind.Agents.Fleet;

namespace Dragonmind.Agents.Handlers;

/// <summary>
/// What a handler gives back: prose for the operator, and the state changes it decided on.
/// </summary>
/// <param name="Message">The reply.</param>
/// <param name="Changes">
/// Changes the turn should make, for the orchestrator to apply. A handler returns them rather than
/// applying them so a test can assert on what a turn decided, not only on where it ended up —
/// "did nothing" and "did two things that cancelled out" are different bugs.
/// </param>
public sealed record TurnResponse(string Message, IReadOnlyList<StateChange> Changes)
{
    /// <summary>A reply that changes nothing.</summary>
    public static TurnResponse Say(string message) => new(message, []);
}
