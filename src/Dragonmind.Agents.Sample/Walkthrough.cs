using Dragonmind.Agents;

namespace Dragonmind.Agents.Sample;

/// <summary>
/// One turn of the scripted walkthrough: what the operator says, and the replies the scripted client
/// hands back for that turn.
/// </summary>
/// <param name="Request">The operator's words.</param>
/// <param name="Note">A line printed before the turn, when the driver is doing something worth saying.</param>
/// <param name="OpenChangeWindow">Sets the change window before the turn runs, when not null.</param>
/// <param name="Classification">The reply the classifier's scripted client returns.</param>
/// <param name="HandlerReply">The reply the handler's scripted client returns, when one runs.</param>
public sealed record WalkthroughTurn(
    string Request,
    string? Note,
    bool? OpenChangeWindow,
    string Classification,
    string? HandlerReply);

/// <summary>
/// The default run: one turn per intent, the same request refused and then carried out, and one
/// answer that does not satisfy its own schema.
/// </summary>
/// <remarks>
/// These are canned model replies, written by hand. They are what makes the sample runnable with no
/// key and no network — and, just as importantly, what makes the run identical every time, so the
/// transcript in the README is something that can be regenerated and diffed rather than trusted.
/// </remarks>
public static class Walkthrough
{
    /// <summary>The turns, in order.</summary>
    public static IReadOnlyList<WalkthroughTurn> Turns { get; } =
    [
        new WalkthroughTurn(
            Request: "what depends on payments-db?",
            Note: null,
            OpenChangeWindow: false,
            Classification: """
                intent: StateQuery
                question: which services depend on payments-db
                """,
            HandlerReply: """
                message: orders-api depends on payments-db. payments-db is degraded, so orders-api is exposed to that.
                """),

        new WalkthroughTurn(
            Request: "restart payments-db",
            Note: "The change window is CLOSED.",
            OpenChangeWindow: false,
            Classification: """
                intent: Action
                actions[1]{verb,target,parameter}:
                  restart,payments-db,
                """,
            HandlerReply: """
                message: I have not restarted payments-db. The change window is closed, so no change to the fleet can be made right now.
                """),

        new WalkthroughTurn(
            Request: "restart payments-db",
            Note: "Same words as the turn above. The change window is now OPEN - and nothing else changed.",
            OpenChangeWindow: true,
            Classification: """
                intent: Action
                actions[1]{verb,target,parameter}:
                  restart,payments-db,
                """,
            HandlerReply: """
                decision: allow
                message: Restarting payments-db. orders-api depends on it and will see a brief interruption.
                """),

        new WalkthroughTurn(
            Request: "orders-api talks to ledger-service as well, not just payments-db",
            Note: "The steward already believes orders-api depends on payments-db. This turn reads that "
                + "belief before adding to it.",
            OpenChangeWindow: null,
            Classification: """
                intent: Correction
                subject: orders-api
                claim: orders-api depends only on payments-db
                correction: orders-api also depends on ledger-service
                """,
            HandlerReply: """
                message: Noted. I will treat orders-api as depending on both payments-db and ledger-service.
                predicate: DEPENDS_ON
                object: ledger-service
                """),

        new WalkthroughTurn(
            Request: "save this as post-rollout",
            Note: null,
            OpenChangeWindow: null,
            Classification: """
                intent: Checkpoint
                label: post-rollout
                """,
            HandlerReply: """
                message: Recorded the fleet as it stands under "post-rollout".
                """),

        new WalkthroughTurn(
            Request: "do the thing with the stuff",
            Note: "The classifier answers with an action naming no action - off-schema for the kind it chose.",
            OpenChangeWindow: null,
            Classification: """
                intent: Action
                actions[0]:
                """,
            // No handler reply, because no handler runs. The turn ends at validation.
            HandlerReply: null)
    ];

    /// <summary>
    /// Builds the per-role scripts the scripted clients replay, in call order.
    /// </summary>
    /// <remarks>
    /// A handler's script only receives a reply for the turns that reach it, which is why the
    /// routing has to be worked out here too. If that calculation and the routing policy ever
    /// disagreed, a scripted client would run dry and throw — loudly, by design, rather than
    /// returning an empty response that would make the walkthrough look fine.
    /// </remarks>
    public static IReadOnlyDictionary<string, string[]> BuildScript()
    {
        List<string> classifier = [];
        List<string> explainer = [];
        List<string> policy = [];
        List<string> state = [];

        foreach (var turn in Turns)
        {
            classifier.Add(turn.Classification);

            if (turn.HandlerReply is null)
            {
                continue;
            }

            // Which handler gets this reply follows from the intent and the window, exactly as the
            // routing policy decides it at run time.
            var open = turn.OpenChangeWindow ?? true;

            if (turn.Classification.Contains("intent: Action", StringComparison.Ordinal))
            {
                (open ? policy : explainer).Add(turn.HandlerReply);
            }
            else if (turn.Classification.Contains("intent: StateQuery", StringComparison.Ordinal))
            {
                explainer.Add(turn.HandlerReply);
            }
            else
            {
                state.Add(turn.HandlerReply);
            }
        }

        return new Dictionary<string, string[]>
        {
            [AgentRole.Classifier] = [.. classifier],
            [AgentRole.Explainer] = [.. explainer],
            [AgentRole.Policy] = [.. policy],
            [AgentRole.State] = [.. state]
        };
    }
}
