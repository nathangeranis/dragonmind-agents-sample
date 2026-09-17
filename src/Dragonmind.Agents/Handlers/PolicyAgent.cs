using Dragonmind.Agents.Fleet;
using Dragonmind.Agents.Toon;

using Microsoft.Extensions.AI;

namespace Dragonmind.Agents.Handlers;

/// <summary>
/// Evaluates a requested change against what the fleet can safely take right now.
/// </summary>
public interface IPolicyAgent
{
    /// <summary>Judges the requested actions and produces the reply.</summary>
    Task<TurnResponse> EvaluateAsync(PolicyRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// The policy handler. Reached only when the change window is open — whether changes are permitted
/// at all is settled before this agent runs, and is not something it is asked about.
/// </summary>
/// <remarks>
/// The split inside this handler is worth noticing, because it is the same split as the one between
/// the classifier and the router, one level down. The model makes the judgement that needs context
/// ("payments-db is down and two services depend on it, so restarting them now will not help"), and
/// code makes the state changes that must be exact. Nothing the model says is parsed back into a
/// version number or a health value; it says allow or refuse, and the changes are derived from the
/// actions that were already parsed and validated upstream.
/// </remarks>
public sealed class PolicyAgent(IChatClient chatClient) : IPolicyAgent
{
    /// <summary>The response shape for this agent, and only this agent.</summary>
    private const string ResponseSchema = """
        decision: allow | refuse
        message: one or two sentences, addressed to the operator
        """;

    private const float Temperature = 0.2f;

    private const string Allow = "allow";

    private readonly IChatClient _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));

    private sealed class Reply
    {
        public string? Decision { get; init; }

        public string? Message { get; init; }
    }

    /// <inheritdoc />
    public async Task<TurnResponse> EvaluateAsync(PolicyRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var system = $"""
            You are a steward for a fleet of running services, deciding whether a requested change should go ahead now.

            Allow it unless the fleet gives you a concrete reason not to: a dependency that is down or degraded, or a change that would take down something others rely on. Judge only what is in front of you. Do not invent policies, approvals or maintenance rules that are not stated here.

            Reply in TOON, matching this shape exactly and adding no other fields:
            {ResponseSchema}
            """;

        var user = $"""
            {ToonCodec.Array("requested", [.. request.Actions.Select(Describe)])}
            {ToonCodec.Array("fleet", request.Fleet)}
            {ToonCodec.Array("recalled", request.Recalled)}
            """;

        List<ChatMessage> messages =
        [
            new(ChatRole.System, system),
            new(ChatRole.User, user)
        ];

        var response = await _chatClient
            .GetResponseAsync(messages, new ChatOptions { Temperature = Temperature }, cancellationToken)
            .ConfigureAwait(false);

        if (!ToonCodec.TryDecode<Reply>(response?.Text, out var reply) || string.IsNullOrWhiteSpace(reply?.Message))
        {
            // Failing closed is the only safe default here: an unreadable judgement is not an
            // approval, and this is the one handler whose output changes the fleet.
            return TurnResponse.Say("I could not evaluate that change, so I have not made it. Please ask again.");
        }

        var allowed = string.Equals(reply.Decision?.Trim(), Allow, StringComparison.OrdinalIgnoreCase);

        return allowed
            ? new TurnResponse(reply.Message.Trim(), [.. request.Actions.SelectMany(ToChanges)])
            : TurnResponse.Say(reply.Message.Trim());
    }

    private static string Describe(Classification.PlannedAction action) =>
        action.Parameter is null
            ? $"{action.Verb} {action.Target}"
            : $"{action.Verb} {action.Target} to {action.Parameter}";

    /// <summary>
    /// Turns an approved action into state changes. Derived from the validated action, never parsed
    /// back out of the model's prose — a version number read out of a sentence is a version number
    /// nobody checked.
    /// </summary>
    private static IEnumerable<StateChange> ToChanges(Classification.PlannedAction action)
    {
        switch (action.Verb.ToUpperInvariant())
        {
            case "DEPLOY":
            case "UPGRADE":
                if (!string.IsNullOrWhiteSpace(action.Parameter))
                {
                    yield return new StateChange.VersionChanged(action.Target, action.Parameter);
                }

                yield return new StateChange.HealthChanged(action.Target, ServiceHealth.Healthy);
                break;

            case "RESTART":
                yield return new StateChange.HealthChanged(action.Target, ServiceHealth.Healthy);
                break;

            case "STOP":
                yield return new StateChange.HealthChanged(action.Target, ServiceHealth.Down);
                break;

            default:
                // A verb with no mechanical meaning still gets an approving reply; it simply does
                // not fabricate a state change to go with it.
                break;
        }
    }
}
