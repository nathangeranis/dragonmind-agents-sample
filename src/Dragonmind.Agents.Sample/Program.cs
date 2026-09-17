using System.ClientModel;

using Dragonmind.Agents;
using Dragonmind.Agents.Classification;
using Dragonmind.Agents.Fleet;
using Dragonmind.Agents.Knowledge;
using Dragonmind.Agents.Sample;
using Dragonmind.Agents.Turn;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

using OpenAI;

// =============================================================================================
// The composition root, and the only place that knows a model provider exists.
//
// With nothing configured this runs the scripted walkthrough: no API key, no network, no database.
// Setting AGENTS_SAMPLE_MODEL_ENDPOINT swaps every agent to a real provider through the standard
// Microsoft.Extensions.AI registration. Not one line of agent code differs between the two, because
// an agent resolves its client by role key and never learns what is behind it.
// =============================================================================================

var endpoint = Environment.GetEnvironmentVariable("AGENTS_SAMPLE_MODEL_ENDPOINT");
var apiKey = Environment.GetEnvironmentVariable("AGENTS_SAMPLE_MODEL_API_KEY");
var model = Environment.GetEnvironmentVariable("AGENTS_SAMPLE_MODEL");
var live = !string.IsNullOrWhiteSpace(endpoint);

var services = new ServiceCollection();

if (live)
{
    if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(model))
    {
        Console.Error.WriteLine(
            "AGENTS_SAMPLE_MODEL_ENDPOINT is set, so AGENTS_SAMPLE_MODEL_API_KEY and AGENTS_SAMPLE_MODEL must be set too.");
        return 1;
    }

    // Any OpenAI-compatible endpoint. No provider is named here and none is the default: the
    // endpoint, the key and the model all arrive from the environment, so this repository contains
    // no credential and no opinion about whose model you point it at.
    var client = new OpenAIClient(
        new ApiKeyCredential(apiKey),
        new OpenAIClientOptions { Endpoint = new Uri(endpoint!) });

    foreach (var role in AgentRole.All)
    {
        services.AddKeyedSingleton<IChatClient>(role, client.GetChatClient(model).AsIChatClient());
    }
}
else
{
    services.AddScriptedChatClients(Walkthrough.BuildScript());
}

// The memory. In-process by default so the walkthrough needs nothing installed; the real
// knowledge layer - pgvector for passages, Apache AGE for facts - is one compose profile away and
// is what the integration tests run against.
services.AddSingleton<IScopedKnowledge, InMemoryKnowledge>();
services.AddFleetSteward();

using var provider = services.BuildServiceProvider();
var orchestrator = provider.GetRequiredService<TurnOrchestrator>();
var knowledge = provider.GetRequiredService<IScopedKnowledge>();

// What the steward already believes when the run starts. Seeded rather than assumed so the
// Correction turn has something real to overturn - and so the difference between the fleet (what
// is deployed) and the graph (what the steward has been told) is visible at both ends of the run.
await knowledge.RelateAsync("orders-api", "DEPENDS_ON", "payments-db");
await knowledge.RememberAsync(
    "payments-db has been degraded since the 11.2 upgrade; orders-api is the only caller.",
    "runbook");

var fleet = new FleetState(
    [
        new ServiceRecord("orders-api", "3.1.0", ServiceHealth.Healthy),
        new ServiceRecord("payments-db", "11.2", ServiceHealth.Degraded),
        new ServiceRecord("ledger-service", "1.4.2", ServiceHealth.Healthy)
    ],
    ChangeWindowOpen: false,
    RecentTurns: []);

Console.WriteLine(live
    ? "fleet steward - live model, scripted walkthrough prompts"
    : "fleet steward - scripted model, no key and no network");
Console.WriteLine();

foreach (var turn in Walkthrough.Turns)
{
    if (turn.OpenChangeWindow is { } open)
    {
        fleet = fleet with { ChangeWindowOpen = open };
    }

    if (turn.Note is not null)
    {
        Console.WriteLine($"  [{turn.Note}]");
    }

    Console.WriteLine($"> {turn.Request}");

    var outcome = await orchestrator.RunAsync(new Interaction(turn.Request), fleet);
    fleet = outcome.State;

    // The two lines that carry the whole point: what the model decided, and where the code sent it.
    Console.WriteLine($"  intent  : {outcome.Intent?.ToString() ?? "(not classified)"}");
    Console.WriteLine($"  handler : {outcome.Handler?.ToString() ?? "(none ran)"}");

    foreach (var change in outcome.Changes)
    {
        Console.WriteLine($"  change  : {change}");
    }

    Console.WriteLine($"  {outcome.Reply}");
    Console.WriteLine();
}

Console.WriteLine("fleet after the run:");

foreach (var service in fleet.Services)
{
    Console.WriteLine($"  {service.Name} {service.Version} {service.Health}");
}

Console.WriteLine();
Console.WriteLine("what the steward now believes about orders-api:");

foreach (var fact in await knowledge.RelatedFactsAsync("orders-api"))
{
    Console.WriteLine($"  {fact.Subject} {fact.Predicate} {fact.Object}");
}

return 0;
