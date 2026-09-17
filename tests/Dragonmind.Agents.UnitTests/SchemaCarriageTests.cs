using Dragonmind.Agents.Classification;
using Dragonmind.Agents.Fleet;
using Dragonmind.Agents.Handlers;
using Dragonmind.Agents.Knowledge;
using Dragonmind.Agents.Routing;
using Dragonmind.Agents.Scripted;
using Dragonmind.Agents.Turn;

using Microsoft.Extensions.AI;

using Moq;

namespace Dragonmind.Agents.UnitTests;

/// <summary>
/// Asserts the token-spend claim directly: every agent call carries the response schema for its own
/// request type, and no others. Only the classifier carries all four, because choosing between them
/// is its job.
/// </summary>
/// <remarks>
/// This is checked on the prompts actually sent, recovered from <see cref="ScriptedChatClient.Calls"/>,
/// rather than by reading the prompt constants. A schema accidentally appended to a handler's prompt
/// costs tokens on every single call and would be invisible to any test that only looked at what
/// came back.
/// </remarks>
public class SchemaCarriageTests
{
    private static FleetState Fleet(bool changeWindowOpen) => new(
        [new ServiceRecord("orders-api", "3.1.0", ServiceHealth.Healthy)],
        changeWindowOpen,
        []);

    private static Mock<IScopedKnowledge> QuietKnowledge()
    {
        var knowledge = new Mock<IScopedKnowledge>();

        knowledge
            .Setup(k => k.RecallAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        knowledge
            .Setup(k => k.RelatedFactsAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        knowledge
            .Setup(k => k.RelateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        return knowledge;
    }

    /// <summary>
    /// Runs one turn and hands back the prompt each agent was sent, so the assertions below read
    /// against the real thing rather than against a constant.
    /// </summary>
    private static async Task<(string Classifier, string? Handler)> PromptsForTurnAsync(
        string classification,
        string handlerReply,
        bool changeWindowOpen)
    {
        var classifier = new ScriptedChatClient(AgentRole.Classifier, classification);
        var explainer = new ScriptedChatClient(AgentRole.Explainer, handlerReply);
        var policy = new ScriptedChatClient(AgentRole.Policy, handlerReply);
        var state = new ScriptedChatClient(AgentRole.State, handlerReply);
        var knowledge = QuietKnowledge();

        var orchestrator = new TurnOrchestrator(
            new IntentAgent(classifier),
            new RoutePolicy(),
            new ExplainerAgent(explainer),
            new PolicyAgent(policy),
            new StateAgent(state, knowledge.Object),
            knowledge.Object);

        await orchestrator.RunAsync(new Interaction("a turn"), Fleet(changeWindowOpen));

        var handler = new[] { explainer, policy, state }.SingleOrDefault(c => c.CallCount > 0);

        return (SystemPromptOf(classifier), handler is null ? null : SystemPromptOf(handler));
    }

    private static string SystemPromptOf(ScriptedChatClient client)
    {
        var call = Assert.Single(client.Calls);

        return call.Single(m => m.Role == ChatRole.System).Text;
    }

    /// <summary>The marker each intent's worked example opens with.</summary>
    private static string Tag(Intent intent) => $"intent: {intent}";

    [Fact]
    public async Task TheClassifierCarriesEveryIntentShape()
    {
        // It must: it is choosing between them. This is the one call that legitimately pays for the
        // whole menu, and the assertion exists so that stays a deliberate exception rather than the
        // default everything drifts towards.
        var (classifier, _) = await PromptsForTurnAsync(
            "intent: Checkpoint\nlabel: a label",
            "message: recorded",
            changeWindowOpen: true);

        foreach (var intent in Enum.GetValues<Intent>())
        {
            Assert.Contains(Tag(intent), classifier, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task TheExplainerCarriesOnlyItsOwnShape()
    {
        var (_, handler) = await PromptsForTurnAsync(
            "intent: StateQuery\nquestion: what is deployed",
            "message: this is deployed",
            changeWindowOpen: true);

        Assert.NotNull(handler);
        Assert.Contains("message:", handler, StringComparison.Ordinal);

        // Not the policy handler's decision field, not the state handler's relationship fields, and
        // not the four classification shapes.
        Assert.DoesNotContain("decision:", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("predicate:", handler, StringComparison.Ordinal);
        AssertCarriesNoClassificationShapes(handler);
    }

    [Fact]
    public async Task ThePolicyHandlerCarriesOnlyItsOwnShape()
    {
        var (_, handler) = await PromptsForTurnAsync(
            "intent: Action\nactions[1]{verb,target,parameter}:\n  restart,orders-api,",
            "decision: allow\nmessage: done",
            changeWindowOpen: true);

        Assert.NotNull(handler);
        Assert.Contains("decision:", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("predicate:", handler, StringComparison.Ordinal);
        AssertCarriesNoClassificationShapes(handler);
    }

    [Fact]
    public async Task TheStateHandlerCarriesOnlyItsOwnShape()
    {
        var (_, handler) = await PromptsForTurnAsync(
            "intent: Correction\nsubject: orders-api\nclaim: a\ncorrection: b",
            "message: noted\npredicate: DEPENDS_ON\nobject: payments-db",
            changeWindowOpen: false);

        Assert.NotNull(handler);
        Assert.Contains("predicate:", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("decision:", handler, StringComparison.Ordinal);
        AssertCarriesNoClassificationShapes(handler);
    }

    [Fact]
    public async Task TheCheckpointShapeIsSmallerThanTheCorrectionShape()
    {
        // Two intents share the state handler, and each still gets only what it needs. A checkpoint
        // is confirmed in prose and has no relationship to record, so carrying the correction's
        // fields here would be paying for three lines that this call can never use.
        var (_, handler) = await PromptsForTurnAsync(
            "intent: Checkpoint\nlabel: before the rollout",
            "message: recorded",
            changeWindowOpen: true);

        Assert.NotNull(handler);
        Assert.Contains("message:", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("predicate:", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("object:", handler, StringComparison.Ordinal);
    }

    private static void AssertCarriesNoClassificationShapes(string prompt)
    {
        foreach (var intent in Enum.GetValues<Intent>())
        {
            Assert.DoesNotContain(Tag(intent), prompt, StringComparison.Ordinal);
        }
    }
}
