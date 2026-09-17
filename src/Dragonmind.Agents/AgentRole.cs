namespace Dragonmind.Agents;

/// <summary>
/// The dependency-injection keys under which each agent's chat client is registered.
/// </summary>
/// <remarks>
/// <para>
/// Every agent takes an <c>IChatClient</c> and resolves it by one of these keys, so which client an
/// agent talks to is a composition-root decision and nothing an agent can reach around. The sample
/// registers a scripted client under all four; setting one environment variable swaps the same four
/// keys to a real provider, and not one line of agent code changes.
/// </para>
/// <para>
/// Keying by registration rather than by a tag passed in the request is deliberate. A tag would put
/// a value in the production call path whose only consumer is a test double, and a scripted client
/// that instead matched on message content would be reading prompts to decide how to behave — which
/// is precisely the kind of implicit coupling the rest of this repository exists to argue against.
/// </para>
/// </remarks>
public static class AgentRole
{
    /// <summary>Classifies a turn. See <c>IIntentAgent</c>.</summary>
    public const string Classifier = "classifier";

    /// <summary>Answers questions and delivers refusals.</summary>
    public const string Explainer = "explainer";

    /// <summary>Evaluates a requested change against policy.</summary>
    public const string Policy = "policy";

    /// <summary>Reads and writes what the steward knows.</summary>
    public const string State = "state";

    /// <summary>Every role, so a host can register or enumerate them without repeating the list.</summary>
    public static IReadOnlyList<string> All { get; } = [Classifier, Explainer, Policy, State];
}
