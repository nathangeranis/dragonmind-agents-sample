namespace Dragonmind.Agents.Classification;

/// <summary>
/// What an operator's turn is asking for. This is the only thing the classifying agent decides.
/// </summary>
/// <remarks>
/// Note what is not here: any mention of which agent handles the turn. The classifier answers
/// "what kind of request is this", and code answers "who handles it" — see <c>RoutePolicy</c>.
/// An architecture test asserts that no member of <see cref="IntentResult"/> is typed
/// <c>HandlerId</c>, so the separation cannot erode by accident.
/// </remarks>
public enum Intent
{
    /// <summary>Asks for something to be done to the fleet. Carries the parsed actions.</summary>
    Action,

    /// <summary>Asks a question about the fleet. Changes nothing.</summary>
    StateQuery,

    /// <summary>Asserts that something the steward believes is wrong.</summary>
    Correction,

    /// <summary>Asks for the current state to be recorded under a label.</summary>
    Checkpoint
}

/// <summary>
/// One action an operator asked for, parsed out of their request.
/// </summary>
/// <param name="Verb">What to do, e.g. <c>restart</c>, <c>deploy</c>, <c>scale</c>.</param>
/// <param name="Target">The service it applies to.</param>
/// <param name="Parameter">Any argument the verb needs, e.g. a version. Null when it needs none.</param>
public sealed record PlannedAction(string Verb, string Target, string? Parameter);

/// <summary>
/// A successful classification: the intent, plus whatever that intent's own schema carries.
/// </summary>
/// <remarks>
/// Each intent has a different payload, so this is a closed hierarchy rather than one record with
/// four mostly-null properties. A handler receives the concrete type it can actually use, and a
/// missing case is a compile-time gap rather than a null at runtime.
/// </remarks>
public abstract record IntentResult
{
    private IntentResult()
    {
    }

    /// <summary>Which intent this is.</summary>
    public abstract Intent Intent { get; }

    /// <summary>Do something to the fleet.</summary>
    /// <param name="Actions">The actions parsed from the request, in the order given.</param>
    public sealed record ActionRequested(IReadOnlyList<PlannedAction> Actions) : IntentResult
    {
        /// <inheritdoc />
        public override Intent Intent => Intent.Action;
    }

    /// <summary>Answer a question about the fleet.</summary>
    /// <param name="Question">The question, normalised by the classifier.</param>
    public sealed record StateQueried(string Question) : IntentResult
    {
        /// <inheritdoc />
        public override Intent Intent => Intent.StateQuery;
    }

    /// <summary>
    /// Correct something the steward believes.
    /// </summary>
    /// <param name="Subject">The entity the claim is about — the name looked up in the knowledge
    /// graph before anything is written.</param>
    /// <param name="Claim">What the steward currently believes, as the operator characterised it.</param>
    /// <param name="Correction">What the operator says is true instead.</param>
    public sealed record CorrectionOffered(string Subject, string Claim, string Correction) : IntentResult
    {
        /// <inheritdoc />
        public override Intent Intent => Intent.Correction;
    }

    /// <summary>Record the current state under a label.</summary>
    /// <param name="Label">The operator's name for this checkpoint.</param>
    public sealed record CheckpointRequested(string Label) : IntentResult
    {
        /// <inheritdoc />
        public override Intent Intent => Intent.Checkpoint;
    }
}

/// <summary>
/// The outcome of asking the classifier to read a turn: either a validated result, or a refusal.
/// </summary>
/// <remarks>
/// There is deliberately no third state and no fallback. A classifier that cannot produce output
/// matching one of its schemas has not given an answer, and turning that into a guess — "treat it
/// as an action" — is how a malformed response becomes a change to the fleet. The orchestrator
/// asks the operator again instead.
/// </remarks>
public abstract record IntentClassification
{
    private IntentClassification()
    {
    }

    /// <summary>The turn was classified and its payload validated against that intent's schema.</summary>
    public sealed record Classified(IntentResult Result) : IntentClassification;

    /// <summary>
    /// The turn was not classified. <paramref name="Reason"/> is for the operator and the log; it
    /// is never a partial result to be acted on.
    /// </summary>
    public sealed record Unclassified(string Reason) : IntentClassification;
}
