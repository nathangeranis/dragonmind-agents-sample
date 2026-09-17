using Dragonmind.Agents.Classification;
using Dragonmind.Agents.Fleet;

namespace Dragonmind.Agents.Handlers;

/// <summary>
/// What the explainer needs. Built by its own adapter and by nothing else.
/// </summary>
/// <param name="Question">
/// The thing to answer. On a refusal route this is still the operator's original request — the
/// explainer is being asked to explain why it is not happening, which requires knowing what "it" was.
/// </param>
/// <param name="Refusal">
/// The policy's reason for refusing, or <see langword="null"/> on an ordinary question. The
/// explainer states this reason; it does not decide it, and it has no way to decide otherwise.
/// </param>
/// <param name="Fleet">The fleet, already rendered. The explainer never sees <see cref="FleetState"/>.</param>
/// <param name="Recalled">Passages retrieved for this question, already selected by the adapter.</param>
/// <param name="Facts">
/// Relationships retrieved from the knowledge graph for the services the question names. Dependency
/// information lives only in the graph, so without this the explainer could not answer "what
/// depends on X" at all — which is the adapter's decision to make, not the handler's.
/// </param>
public sealed record ExplainRequest(
    string Question,
    string? Refusal,
    IReadOnlyList<string> Fleet,
    IReadOnlyList<string> Recalled,
    IReadOnlyList<string> Facts);

/// <summary>
/// What the policy handler needs: the actions to judge, and the fleet context to judge them against.
/// </summary>
/// <param name="Actions">The parsed actions, exactly as classified.</param>
/// <param name="Fleet">The fleet, already rendered.</param>
/// <param name="Recalled">Passages retrieved about the targeted services.</param>
public sealed record PolicyRequest(
    IReadOnlyList<PlannedAction> Actions,
    IReadOnlyList<string> Fleet,
    IReadOnlyList<string> Recalled);

/// <summary>
/// What the state handler needs. Two intents route here, so this is a closed pair rather than one
/// record with two half-populated halves.
/// </summary>
/// <remarks>
/// This type is where "two intents, one handler, two adapters" stops being an assertion and becomes
/// something the compiler enforces. The handler switches on the case it was given; neither adapter
/// can produce the other's shape, and neither case carries a field the other needs.
/// </remarks>
public abstract record StateRequest
{
    private StateRequest()
    {
    }

    /// <summary>
    /// Correct something the steward believes.
    /// </summary>
    /// <param name="Subject">The entity to look up before anything is written.</param>
    /// <param name="Claim">What the operator says is currently believed.</param>
    /// <param name="Correction">What is true instead.</param>
    /// <param name="KnownFacts">
    /// What the graph already holds about <paramref name="Subject"/>, retrieved by the adapter
    /// before the handler ran. Its presence in the request is what makes "consult before mutating"
    /// structural rather than a rule someone has to remember.
    /// </param>
    public sealed record ApplyCorrection(
        string Subject,
        string Claim,
        string Correction,
        IReadOnlyList<string> KnownFacts) : StateRequest;

    /// <summary>
    /// Record the current state under a label.
    /// </summary>
    /// <param name="Label">The operator's name for it.</param>
    /// <param name="Fleet">The fleet, already rendered, as it stands at this moment.</param>
    public sealed record RecordCheckpoint(string Label, IReadOnlyList<string> Fleet) : StateRequest;
}
