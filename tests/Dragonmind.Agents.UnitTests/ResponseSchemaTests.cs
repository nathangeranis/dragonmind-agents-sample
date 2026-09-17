using Dragonmind.Agents.Classification;

namespace Dragonmind.Agents.UnitTests;

/// <summary>
/// Pins each intent's response schema: that its own worked example satisfies it, and that the
/// malformed shapes it exists to reject are rejected with a reason rather than bound to something
/// a handler would then act on.
/// </summary>
public class ResponseSchemaTests
{
    [Theory]
    [InlineData(Intent.Action)]
    [InlineData(Intent.StateQuery)]
    [InlineData(Intent.Correction)]
    [InlineData(Intent.Checkpoint)]
    public void EveryIntentHasASchema(Intent intent)
    {
        Assert.True(ResponseSchema.All.ContainsKey(intent), $"no schema for {intent}");
    }

    [Theory]
    [InlineData(Intent.Action)]
    [InlineData(Intent.StateQuery)]
    [InlineData(Intent.Correction)]
    [InlineData(Intent.Checkpoint)]
    public void TheWorkedExampleSatisfiesItsOwnSchema(Intent intent)
    {
        // The example is what teaches the model the shape. If it does not itself bind, the schema
        // is asking for something the decoder cannot produce, and every response of that intent
        // fails for a reason no amount of prompt tuning can fix.
        var schema = ResponseSchema.All[intent];

        Assert.True(
            schema.TryBind(schema.Example, out var result, out var error),
            $"{intent} schema rejected its own example: {error}");

        Assert.NotNull(result);
        Assert.Equal(intent, result.Intent);
    }

    [Theory]
    [InlineData(Intent.Action)]
    [InlineData(Intent.StateQuery)]
    [InlineData(Intent.Correction)]
    [InlineData(Intent.Checkpoint)]
    public void TheWorkedExampleCarriesAnIntentTagThatReadsBack(Intent intent)
    {
        var schema = ResponseSchema.All[intent];

        Assert.True(ResponseSchema.TryReadIntent(schema.Example, out var tagged));
        Assert.Equal(intent, tagged);
    }

    [Fact]
    public void ActionSchema_BindsEveryActionInATabularBlock()
    {
        const string toon = """
            intent: Action
            actions[2]{verb,target,parameter}:
              restart,orders-api,
              deploy,payments-db,4.2.0
            """;

        Assert.True(ResponseSchema.All[Intent.Action].TryBind(toon, out var result, out _));

        var actions = Assert.IsType<IntentResult.ActionRequested>(result).Actions;
        Assert.Equal(2, actions.Count);
        Assert.Equal(new PlannedAction("restart", "orders-api", null), actions[0]);
        Assert.Equal(new PlannedAction("deploy", "payments-db", "4.2.0"), actions[1]);
    }

    [Fact]
    public void ActionSchema_RejectsAnEmptyActionList()
    {
        // "Do something" with no something. Binding this would hand a handler an empty plan, and
        // the turn would report success having changed nothing.
        Assert.False(ResponseSchema.All[Intent.Action].TryBind(
            """
            intent: Action
            actions[0]:
            """,
            out var result,
            out var error));

        Assert.Null(result);
        Assert.NotNull(error);
    }

    [Fact]
    public void ActionSchema_RejectsAnActionMissingItsTarget()
    {
        Assert.False(ResponseSchema.All[Intent.Action].TryBind(
            """
            intent: Action
            actions[1]{verb,target,parameter}:
              restart,,
            """,
            out var result,
            out var error));

        Assert.Null(result);
        Assert.Contains("target", error, StringComparison.Ordinal);
    }

    [Fact]
    public void CorrectionSchema_RejectsACorrectionWithNoSubject()
    {
        // Without a subject there is nothing to look up in the knowledge graph, so the check that
        // makes a correction safe cannot run. Refusing beats writing unverified.
        Assert.False(ResponseSchema.All[Intent.Correction].TryBind(
            """
            intent: Correction
            claim: something is wrong
            correction: something else is right
            """,
            out var result,
            out var error));

        Assert.Null(result);
        Assert.Contains("subject", error, StringComparison.Ordinal);
    }

    [Fact]
    public void CorrectionSchema_RejectsACorrectionThatSaysOnlyWhatIsWrong()
    {
        Assert.False(ResponseSchema.All[Intent.Correction].TryBind(
            """
            intent: Correction
            subject: orders-api
            claim: orders-api depends on payments-db
            """,
            out _,
            out var error));

        Assert.NotNull(error);
    }

    [Fact]
    public void StateQuerySchema_RejectsAnEmptyQuestion()
    {
        Assert.False(ResponseSchema.All[Intent.StateQuery].TryBind(
            """
            intent: StateQuery
            question:
            """,
            out _,
            out var error));

        Assert.NotNull(error);
    }

    [Fact]
    public void CheckpointSchema_RejectsAnEmptyLabel()
    {
        Assert.False(ResponseSchema.All[Intent.Checkpoint].TryBind(
            """
            intent: Checkpoint
            label:
            """,
            out _,
            out var error));

        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("intent: action", Intent.Action)]
    [InlineData("intent: ACTION", Intent.Action)]
    [InlineData("intent:   Correction  ", Intent.Correction)]
    public void TryReadIntent_IsCaseInsensitiveAndTrims(string toon, Intent expected)
    {
        Assert.True(ResponseSchema.TryReadIntent(toon, out var intent));
        Assert.Equal(expected, intent);
    }

    [Theory]
    [InlineData("intent: Escalate")]
    [InlineData("intent:")]
    [InlineData("question: no tag at all")]
    public void TryReadIntent_RefusesAnythingThatIsNotOneOfTheFour(string toon)
    {
        Assert.False(ResponseSchema.TryReadIntent(toon, out _));
    }
}
