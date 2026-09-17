using Dragonmind.Agents;
using Dragonmind.Agents.Classification;
using Dragonmind.Agents.Fleet;
using Dragonmind.Agents.Handlers;
using Dragonmind.Agents.Knowledge;
using Dragonmind.Agents.Routing;
using Dragonmind.Agents.Scripted;
using Dragonmind.Agents.Turn;

using Dragonmind.Core.Application.AntiCorruptionLayer.DTOs;

using Microsoft.Extensions.AI;

using Moq;

namespace Dragonmind.Agents.UnitTests;

/// <summary>
/// End-to-end turns with no model and no database: the scripted client stands in for the model and
/// the knowledge port is a mock.
/// </summary>
public class TurnOrchestratorTests
{
    private static FleetState Fleet(bool changeWindowOpen) => new(
        [
            new ServiceRecord("orders-api", "3.1.0", ServiceHealth.Healthy),
            new ServiceRecord("payments-db", "11.2", ServiceHealth.Degraded)
        ],
        changeWindowOpen,
        []);

    private static Mock<IScopedKnowledge> QuietKnowledge()
    {
        var knowledge = new Mock<IScopedKnowledge>(MockBehavior.Strict);

        knowledge
            .Setup(k => k.RecallAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        knowledge
            .Setup(k => k.RelatedFactsAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        knowledge
            .Setup(k => k.RememberAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        knowledge
            .Setup(k => k.RelateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        return knowledge;
    }

    /// <summary>
    /// Builds an orchestrator whose classifier replays <paramref name="classification"/> and whose
    /// chosen handler replays <paramref name="handlerReply"/>.
    /// </summary>
    private static (TurnOrchestrator Orchestrator, Mock<IScopedKnowledge> Knowledge) Build(
        string classification,
        string handlerReply,
        Mock<IScopedKnowledge>? knowledge = null)
    {
        knowledge ??= QuietKnowledge();

        var orchestrator = new TurnOrchestrator(
            new IntentAgent(new ScriptedChatClient(AgentRole.Classifier, classification)),
            new RoutePolicy(),
            new ExplainerAgent(new ScriptedChatClient(AgentRole.Explainer, handlerReply)),
            new PolicyAgent(new ScriptedChatClient(AgentRole.Policy, handlerReply)),
            new StateAgent(new ScriptedChatClient(AgentRole.State, handlerReply), knowledge.Object),
            knowledge.Object);

        return (orchestrator, knowledge);
    }

    // =========================================================================================
    // One full turn per intent, with no model.
    // =========================================================================================

    [Fact]
    public async Task ActionTurn_WithTheWindowOpen_ReachesPolicyAndChangesTheFleet()
    {
        var (orchestrator, _) = Build(
            """
            intent: Action
            actions[1]{verb,target,parameter}:
              restart,payments-db,
            """,
            """
            decision: allow
            message: Restarting payments-db now.
            """);

        var outcome = await orchestrator.RunAsync(new Interaction("restart payments-db"), Fleet(changeWindowOpen: true));

        Assert.Equal(Intent.Action, outcome.Intent);
        Assert.Equal(HandlerId.Policy, outcome.Handler);
        Assert.Equal("Restarting payments-db now.", outcome.Reply);

        var change = Assert.IsType<StateChange.HealthChanged>(Assert.Single(outcome.Changes));
        Assert.Equal("payments-db", change.Service);
        Assert.Equal(ServiceHealth.Healthy, change.Health);
        Assert.Equal(ServiceHealth.Healthy, outcome.State.Find("payments-db")!.Health);
    }

    [Fact]
    public async Task StateQueryTurn_ReachesTheExplainerAndChangesNothing()
    {
        var (orchestrator, _) = Build(
            """
            intent: StateQuery
            question: what depends on payments-db
            """,
            """
            message: orders-api depends on payments-db.
            """);

        var outcome = await orchestrator.RunAsync(new Interaction("what depends on payments-db?"), Fleet(changeWindowOpen: true));

        Assert.Equal(Intent.StateQuery, outcome.Intent);
        Assert.Equal(HandlerId.Explainer, outcome.Handler);
        Assert.Empty(outcome.Changes);
    }

    [Fact]
    public async Task CorrectionTurn_ReachesTheStateHandler()
    {
        var (orchestrator, knowledge) = Build(
            """
            intent: Correction
            subject: orders-api
            claim: orders-api depends on payments-db
            correction: orders-api no longer depends on payments-db
            """,
            """
            message: Noted - orders-api no longer depends on payments-db.
            predicate: DEPENDS_ON
            object: payments-db
            """);

        var outcome = await orchestrator.RunAsync(new Interaction("orders-api doesn't use payments-db any more"), Fleet(changeWindowOpen: false));

        Assert.Equal(Intent.Correction, outcome.Intent);
        Assert.Equal(HandlerId.State, outcome.Handler);
        knowledge.Verify(k => k.RememberAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CheckpointTurn_ReachesTheStateHandlerAndRecordsASnapshot()
    {
        var (orchestrator, knowledge) = Build(
            """
            intent: Checkpoint
            label: before the payments rollout
            """,
            """
            message: Recorded.
            """);

        var outcome = await orchestrator.RunAsync(new Interaction("save this as before the payments rollout"), Fleet(changeWindowOpen: true));

        Assert.Equal(Intent.Checkpoint, outcome.Intent);
        Assert.Equal(HandlerId.State, outcome.Handler);

        knowledge.Verify(
            k => k.RememberAsync(
                It.Is<string>(s => s.Contains("before the payments rollout", StringComparison.Ordinal)),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // =========================================================================================
    // A change that does not land must not be reported as though it did.
    // =========================================================================================

    [Fact]
    public async Task AnApprovedActionOnAServiceTheFleetDoesNotHaveIsNotRecordedAsAChange()
    {
        // Nothing upstream checks that a parsed action names a real service: the schema only
        // requires a non-blank target, and the policy handler is a model that will not reliably
        // notice a typo. So the orchestrator has to be the place that refuses to claim success.
        var (orchestrator, _) = Build(
            """
            intent: Action
            actions[1]{verb,target,parameter}:
              restart,payments-dbb,
            """,
            """
            decision: allow
            message: Restarting payments-dbb now.
            """);

        var outcome = await orchestrator.RunAsync(new Interaction("restart payments-dbb"), Fleet(changeWindowOpen: true));

        // The change did not move the state, so it is not in Changes. Recording it would make
        // Changes a list of things that may or may not have happened, which is worth nothing.
        Assert.Empty(outcome.Changes);

        // And the fleet really is untouched.
        Assert.Null(outcome.State.Find("payments-dbb"));
        Assert.Equal(ServiceHealth.Degraded, outcome.State.Find("payments-db")!.Health);

        // Most importantly the operator is told. The handler's approving sentence is still there -
        // that is what the model said - but it no longer stands alone.
        Assert.Contains("payments-dbb", outcome.Reply, StringComparison.Ordinal);
        Assert.Contains("Nothing was changed", outcome.Reply, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnApprovedActionOnARealServiceIsStillReportedPlainly()
    {
        // The guard above must not fire on the ordinary path, or every successful action would
        // carry a contradiction after it.
        var (orchestrator, _) = Build(
            """
            intent: Action
            actions[1]{verb,target,parameter}:
              restart,payments-db,
            """,
            """
            decision: allow
            message: Restarting payments-db now.
            """);

        var outcome = await orchestrator.RunAsync(new Interaction("restart payments-db"), Fleet(changeWindowOpen: true));

        Assert.Equal("Restarting payments-db now.", outcome.Reply);
        Assert.Single(outcome.Changes);
    }

    [Fact]
    public async Task AnUnclassifiedTurnIsStillRemembered()
    {
        // RecentRequests exists so the next turn can resolve a back-reference, and the turn most
        // likely to be referred back to is the one the operator is about to rephrase. Every other
        // outcome is recorded; leaving this one out would blind the classifier to exactly that turn.
        var (orchestrator, _) = Build(
            """
            intent: Action
            actions[0]:
            """,
            "unused");

        var outcome = await orchestrator.RunAsync(new Interaction("do the thing"), Fleet(changeWindowOpen: true));

        Assert.Null(outcome.Intent);

        var remembered = Assert.Single(outcome.State.RecentTurns);
        Assert.Equal("do the thing", remembered.Request);
        Assert.Equal(outcome.Reply, remembered.Response);
    }

    // =========================================================================================
    // The state-dependent route: the same text, two destinations.
    // =========================================================================================

    [Fact]
    public async Task TheSameRequestIsCarriedOutWhenTheWindowIsOpenAndRefusedWhenItIsClosed()
    {
        const string sameText = "restart payments-db";
        const string sameClassification = """
            intent: Action
            actions[1]{verb,target,parameter}:
              restart,payments-db,
            """;

        var (openRun, _) = Build(sameClassification, """
            decision: allow
            message: Restarting payments-db now.
            """);

        var (closedRun, _) = Build(sameClassification, """
            message: That will have to wait for the next change window.
            """);

        var opened = await openRun.RunAsync(new Interaction(sameText), Fleet(changeWindowOpen: true));
        var closed = await closedRun.RunAsync(new Interaction(sameText), Fleet(changeWindowOpen: false));

        // Identical text, identical classification - and a different handler, because the window is
        // state the classifier was never shown.
        Assert.Equal(opened.Intent, closed.Intent);
        Assert.Equal(HandlerId.Policy, opened.Handler);
        Assert.Equal(HandlerId.Explainer, closed.Handler);

        Assert.NotEmpty(opened.Changes);
        Assert.Empty(closed.Changes);
    }

    // =========================================================================================
    // Off-schema output never reaches a handler.
    // =========================================================================================

    public static TheoryData<string, string> OffSchemaResponses() => new()
    {
        { "an action request naming no actions", "intent: Action\nactions[0]:" },
        { "an action missing its target", "intent: Action\nactions[1]{verb,target,parameter}:\n  restart,," },
        { "a correction with no subject", "intent: Correction\nclaim: x\ncorrection: y" },
        { "a state query with no question", "intent: StateQuery\nquestion:" },
        { "a checkpoint with no label", "intent: Checkpoint\nlabel:" },
        { "an intent that does not exist", "intent: Escalate\nmessage: help" },
        { "no intent tag at all", "message: I have no idea what shape to use" },
        { "prose instead of structured output", "Sure! I'll restart payments-db for you right away." }
    };

    [Theory]
    [MemberData(nameof(OffSchemaResponses))]
    public async Task OffSchemaClassification_NeverReachesAnyHandler(string because, string classification)
    {
        // Strict mocks: any call at all on any handler fails the test. This is the assertion that
        // makes the ordering in TurnOrchestrator.RunAsync meaningful - validation happens before a
        // route is resolved, so there is no handler to reach yet.
        var explainer = new Mock<IExplainerAgent>(MockBehavior.Strict);
        var policy = new Mock<IPolicyAgent>(MockBehavior.Strict);
        var state = new Mock<IStateAgent>(MockBehavior.Strict);
        var knowledge = new Mock<IScopedKnowledge>(MockBehavior.Strict);

        var orchestrator = new TurnOrchestrator(
            new IntentAgent(new ScriptedChatClient(AgentRole.Classifier, classification)),
            new RoutePolicy(),
            explainer.Object,
            policy.Object,
            state.Object,
            knowledge.Object);

        var outcome = await orchestrator.RunAsync(new Interaction("restart payments-db"), Fleet(changeWindowOpen: true));

        Assert.Null(outcome.Intent);
        Assert.Null(outcome.Handler);
        Assert.Empty(outcome.Changes);
        Assert.Contains("could not tell", outcome.Reply, StringComparison.Ordinal);

        explainer.Verify(h => h.ExplainAsync(It.IsAny<ExplainRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        policy.Verify(h => h.EvaluateAsync(It.IsAny<PolicyRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        state.Verify(h => h.ApplyAsync(It.IsAny<StateRequest>(), It.IsAny<CancellationToken>()), Times.Never);

        // And nothing was retrieved either: no handler means no adapter, and no adapter means the
        // knowledge layer was never touched. Strict mock, zero setups, zero calls.
        knowledge.VerifyNoOtherCalls();

        Assert.False(string.IsNullOrWhiteSpace(because));
    }

    // =========================================================================================
    // Each handler receives only its own adapter-translated input.
    // =========================================================================================

    [Fact]
    public async Task ThePolicyHandlerReceivesOnlyItsOwnRequestShape()
    {
        var policy = new Mock<IPolicyAgent>();
        PolicyRequest? captured = null;

        policy
            .Setup(h => h.EvaluateAsync(It.IsAny<PolicyRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PolicyRequest, CancellationToken>((r, _) => captured = r)
            .ReturnsAsync(TurnResponse.Say("done"));

        var knowledge = QuietKnowledge();

        var orchestrator = new TurnOrchestrator(
            new IntentAgent(new ScriptedChatClient(AgentRole.Classifier, """
                intent: Action
                actions[1]{verb,target,parameter}:
                  deploy,orders-api,3.2.0
                """)),
            new RoutePolicy(),
            Mock.Of<IExplainerAgent>(),
            policy.Object,
            Mock.Of<IStateAgent>(),
            knowledge.Object);

        await orchestrator.RunAsync(new Interaction("deploy orders-api 3.2.0"), Fleet(changeWindowOpen: true));

        Assert.NotNull(captured);

        // It got the parsed actions and a rendered fleet - not FleetState, not the classification,
        // not the route, and nothing belonging to another handler.
        Assert.Equal(new PlannedAction("deploy", "orders-api", "3.2.0"), Assert.Single(captured.Actions));
        Assert.Equal(2, captured.Fleet.Count);
        Assert.All(captured.Fleet, line => Assert.IsType<string>(line));
    }

    [Fact]
    public async Task TheExplainerReceivesTheRefusalReasonAsData()
    {
        var explainer = new Mock<IExplainerAgent>();
        ExplainRequest? captured = null;

        explainer
            .Setup(h => h.ExplainAsync(It.IsAny<ExplainRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ExplainRequest, CancellationToken>((r, _) => captured = r)
            .ReturnsAsync(TurnResponse.Say("no"));

        var orchestrator = new TurnOrchestrator(
            new IntentAgent(new ScriptedChatClient(AgentRole.Classifier, """
                intent: Action
                actions[1]{verb,target,parameter}:
                  restart,orders-api,
                """)),
            new RoutePolicy(),
            explainer.Object,
            Mock.Of<IPolicyAgent>(),
            Mock.Of<IStateAgent>(),
            QuietKnowledge().Object);

        await orchestrator.RunAsync(new Interaction("restart orders-api"), Fleet(changeWindowOpen: false));

        Assert.NotNull(captured);

        // The refusal is data on the request, decided by the policy table. The explainer is told
        // what to say, never asked whether to refuse - there is no field here it could use to
        // reach a different conclusion.
        Assert.Equal(RoutePolicy.ChangeWindowClosed, captured.Refusal);

        // And it still knows what is being refused, or the reason would attach to nothing.
        Assert.Contains("orders-api", captured.Question, StringComparison.Ordinal);
    }

    // =========================================================================================
    // A correction consults the knowledge graph, with the scope already bound, before mutating.
    // =========================================================================================

    [Fact]
    public async Task ACorrectionConsultsTheGraphBeforeWritingAnything()
    {
        var order = new List<string>();
        var knowledge = new Mock<IScopedKnowledge>(MockBehavior.Strict);

        knowledge
            .Setup(k => k.RelatedFactsAsync("orders-api", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("read"))
            .ReturnsAsync([
                new KnowledgeFactDto { Subject = "orders-api", Predicate = "DEPENDS_ON", Object = "payments-db" }
            ]);

        knowledge
            .Setup(k => k.RememberAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("write-note"))
            .Returns(Task.CompletedTask);

        knowledge
            .Setup(k => k.RelateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("write-fact"))
            .ReturnsAsync(true);

        var (orchestrator, _) = Build(
            """
            intent: Correction
            subject: orders-api
            claim: orders-api depends on payments-db
            correction: orders-api talks to the ledger service instead
            """,
            """
            message: Updated.
            predicate: DEPENDS_ON
            object: ledger-service
            """,
            knowledge);

        await orchestrator.RunAsync(new Interaction("orders-api uses the ledger now"), Fleet(changeWindowOpen: false));

        // The read happens first. It is not a convention the handler remembers to follow: the facts
        // are a required field of ApplyCorrection, built by the adapter, so the handler cannot be
        // called at all until the lookup has returned.
        Assert.Equal("read", order[0]);
        Assert.Contains("write-note", order);
        Assert.Contains("write-fact", order);
    }

    [Fact]
    public async Task TheScopeIsBoundOnceAndNeverPassedByAHandler()
    {
        // IScopedKnowledge has no scope parameter anywhere in its surface. This is the test for
        // that: every method a handler can reach is scope-free, so forwarding the wrong scope is
        // not a mistake that can be made.
        var methods = typeof(IScopedKnowledge).GetMethods();

        Assert.NotEmpty(methods);
        Assert.All(methods, m =>
            Assert.DoesNotContain(m.GetParameters(), p =>
                p.ParameterType.Name.Contains("ScopeId", StringComparison.Ordinal)));

        await Task.CompletedTask;
    }

    // =========================================================================================
    // The knowledge layer's relationship vocabulary is finite, and a refusal must be reported.
    // =========================================================================================

    [Fact]
    public async Task ARejectedRelationshipIsReportedRatherThanClaimedAsWritten()
    {
        var knowledge = QuietKnowledge();

        // This is what the real facade does for a predicate outside its vocabulary: returns false,
        // writes nothing, throws nothing. A steward that reported success here would be wrong in
        // exactly the way the correction it was handling existed to fix.
        knowledge
            .Setup(k => k.RelateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var (orchestrator, _) = Build(
            """
            intent: Correction
            subject: orders-api
            claim: orders-api is unrelated to billing
            correction: orders-api is throttled by billing
            """,
            """
            message: Updated.
            predicate: THROTTLED_BY
            object: billing
            """,
            knowledge);

        var outcome = await orchestrator.RunAsync(new Interaction("billing throttles orders-api"), Fleet(changeWindowOpen: false));

        Assert.Contains("THROTTLED_BY", outcome.Reply, StringComparison.Ordinal);
        Assert.Contains("not one I can record", outcome.Reply, StringComparison.Ordinal);
    }

    // =========================================================================================
    // The scripted client must fail loudly rather than quietly returning nothing.
    // =========================================================================================

    [Fact]
    public async Task TheScriptedClientThrowsWhenItRunsOutOfResponses()
    {
        var client = new ScriptedChatClient(AgentRole.Classifier, "intent: Checkpoint\nlabel: one");

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "first")]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "second")]));

        Assert.Contains(AgentRole.Classifier, ex.Message, StringComparison.Ordinal);
    }
}
