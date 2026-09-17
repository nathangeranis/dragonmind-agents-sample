using Dragonmind.Agents.Classification;
using Dragonmind.Agents.Handlers;
using Dragonmind.Agents.Routing;
using Dragonmind.Agents.Scripted;
using Dragonmind.Agents.Turn;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Dragonmind.Agents;

/// <summary>
/// Registers the steward: the classifier, the routing policy, the three handlers and the
/// orchestrator that runs a turn through them.
/// </summary>
public static class AgentServiceExtensions
{
    /// <summary>
    /// Adds the steward. The caller must separately register an <see cref="IChatClient"/> under each
    /// key in <see cref="AgentRole.All"/>, and an <c>IScopedKnowledge</c>.
    /// </summary>
    /// <remarks>
    /// Every agent resolves its chat client by key, so swapping the model provider is entirely a
    /// composition-root decision — see <see cref="AddScriptedChatClients"/> for the no-network
    /// default, and the sample host for the real-provider branch. No agent constructor changes
    /// between the two.
    /// </remarks>
    public static IServiceCollection AddFleetSteward(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IRoutePolicy, RoutePolicy>();

        services.AddSingleton<IIntentAgent>(sp =>
            new IntentAgent(sp.GetRequiredKeyedService<IChatClient>(AgentRole.Classifier)));

        services.AddSingleton<IExplainerAgent>(sp =>
            new ExplainerAgent(sp.GetRequiredKeyedService<IChatClient>(AgentRole.Explainer)));

        services.AddSingleton<IPolicyAgent>(sp =>
            new PolicyAgent(sp.GetRequiredKeyedService<IChatClient>(AgentRole.Policy)));

        services.AddSingleton<IStateAgent>(sp =>
            new StateAgent(
                sp.GetRequiredKeyedService<IChatClient>(AgentRole.State),
                sp.GetRequiredService<Knowledge.IScopedKnowledge>()));

        services.AddSingleton<TurnOrchestrator>();

        return services;
    }

    /// <summary>
    /// Registers a scripted client under every agent role, so the steward runs with no key and no
    /// network. <paramref name="script"/> maps a role to the replies it hands out, in order.
    /// </summary>
    public static IServiceCollection AddScriptedChatClients(
        this IServiceCollection services,
        IReadOnlyDictionary<string, string[]> script)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(script);

        foreach (var role in AgentRole.All)
        {
            var responses = script.TryGetValue(role, out var configured) ? configured : [];

            services.AddKeyedSingleton<IChatClient>(role, new ScriptedChatClient(role, responses));
        }

        return services;
    }
}
