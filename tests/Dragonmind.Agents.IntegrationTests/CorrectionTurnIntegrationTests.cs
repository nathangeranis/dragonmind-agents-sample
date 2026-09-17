using Dragonmind.Agents.Classification;
using Dragonmind.Agents.Fleet;
using Dragonmind.Agents.Handlers;
using Dragonmind.Agents.Routing;
using Dragonmind.Agents.Scripted;
using Dragonmind.Agents.Turn;

using Dragonmind.Core.Domain.SharedIdentities;

using Npgsql;

namespace Dragonmind.Agents.IntegrationTests;

/// <summary>
/// One whole turn against the real knowledge layer: a <c>Correction</c>, which is the only intent
/// that both reads and writes memory.
/// </summary>
/// <remarks>
/// The unit tests prove the orchestration with the knowledge port mocked. This proves the other
/// half — that the port's four calls line up with what the real facade does, that a passage really
/// reaches the <c>vector(1536)</c> column, and that a fact really becomes an edge in the graph.
/// Every assertion is checked by querying the database directly as well as through the facade,
/// because a facade that returned what it was given without persisting it would satisfy a
/// facade-only assertion perfectly.
/// </remarks>
[Collection(StewardCollection.Name)]
public class CorrectionTurnIntegrationTests(StewardFixture fixture)
{
    private static FleetState Fleet() => new(
        [
            new ServiceRecord("orders-api", "3.1.0", ServiceHealth.Healthy),
            new ServiceRecord("payments-db", "11.2", ServiceHealth.Degraded)
        ],
        ChangeWindowOpen: false,
        RecentTurns: []);

    [Fact]
    public async Task ACorrectionTurnReadsTheGraphAndThenWritesThroughTheRealFacade()
    {
        // A fresh scope per run, so this test never sees another run's rows and never leaves rows
        // another run could see. Scope isolation is the knowledge layer's own guarantee; here it is
        // simply what keeps the assertions below exact.
        var scope = ScopeId.New();
        var knowledge = fixture.ScopedTo(scope);

        // What the steward already believes. The correction has to be checked against this.
        Assert.True(await knowledge.RelateAsync("orders-api", "DEPENDS_ON", "payments-db"));

        var orchestrator = new TurnOrchestrator(
            new IntentAgent(new ScriptedChatClient("classifier", """
                intent: Correction
                subject: orders-api
                claim: orders-api depends only on payments-db
                correction: orders-api also depends on ledger-service
                """)),
            new RoutePolicy(),
            new ExplainerAgent(new ScriptedChatClient("explainer")),
            new PolicyAgent(new ScriptedChatClient("policy")),
            new StateAgent(new ScriptedChatClient("state", """
                message: Noted. orders-api depends on both payments-db and ledger-service.
                predicate: DEPENDS_ON
                object: ledger-service
                """), knowledge),
            knowledge);

        var outcome = await orchestrator.RunAsync(
            new Interaction("orders-api talks to ledger-service as well"),
            Fleet());

        Assert.Equal(Intent.Correction, outcome.Intent);
        Assert.Equal(HandlerId.State, outcome.Handler);

        // The graph edge is real: through the facade...
        var facts = await knowledge.RelatedFactsAsync("orders-api");
        var objects = facts.Select(f => f.Object).ToList();

        Assert.Contains("payments-db", objects);
        Assert.Contains("ledger-service", objects);

        // ...and in Apache AGE itself. Queried through the graph's edge parent table, which covers
        // every relationship label, so no label has to be guessed; and filtered on the scope id,
        // which is stored as a plain string in the edge's properties.
        var edges = await CountGraphEdgesAsync(scope);
        Assert.Equal(2, edges);

        // The operator's own words reached the vector store. The hash-derived embedding is
        // deterministic, so searching for the same text scores an exact match and ranks first -
        // an identity round trip through the real pgvector column rather than a ranking that
        // could drift.
        var recalled = await knowledge.RecallAsync("orders-api: orders-api also depends on ledger-service");

        Assert.NotEmpty(recalled);
        Assert.Contains("ledger-service", recalled[0].Content, StringComparison.Ordinal);

        // ...and the row is really in the table, with a vector of the declared width.
        var (documents, width) = await InspectDocumentAsync(scope);
        Assert.Equal(1, documents);
        Assert.Equal(Dragonmind.Core.AI.EmbeddingDimensions.Default, width);
    }

    [Fact]
    public async Task APredicateOutsideTheVocabularyIsRefusedAndNothingIsWritten()
    {
        // The unit tests assert the handler REPORTS a refusal. This asserts the refusal is real:
        // the facade returns false and the graph gains no edge.
        var scope = ScopeId.New();
        var knowledge = fixture.ScopedTo(scope);

        Assert.False(await knowledge.RelateAsync("orders-api", "THROTTLED_BY", "billing"));
        Assert.Equal(0, await CountGraphEdgesAsync(scope));
    }

    private async Task<int> CountGraphEdgesAsync(ScopeId scope)
    {
        await using var command = fixture.DataSource.CreateCommand(
            """
            SELECT count(*)
            FROM knowledge_graph."_ag_label_edge"
            WHERE properties::text LIKE @scope
            """);

        command.Parameters.Add(new NpgsqlParameter("scope", $"%{scope.Value}%"));

        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<(int Documents, int VectorWidth)> InspectDocumentAsync(ScopeId scope)
    {
        await using var command = fixture.DataSource.CreateCommand(
            """
            SELECT count(*), coalesce(max(vector_dims(vector)), 0)
            FROM knowledge.documents
            WHERE scope_id = @scope
            """);

        command.Parameters.Add(new NpgsqlParameter("scope", scope.Value));

        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();

        return (reader.GetInt32(0), reader.GetInt32(1));
    }
}
