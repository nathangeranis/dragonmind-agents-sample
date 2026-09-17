using Dragonmind.Agents.Knowledge;
using Dragonmind.Agents.Toon;

using Microsoft.Extensions.AI;

namespace Dragonmind.Agents.Handlers;

/// <summary>
/// Reads and writes what the steward knows: corrections to its beliefs, and checkpoints of the
/// fleet as it stands.
/// </summary>
public interface IStateAgent
{
    /// <summary>Applies a correction or records a checkpoint.</summary>
    Task<TurnResponse> ApplyAsync(StateRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// The state handler. Two intents route here and each arrives in its own shape, so the handler
/// switches on the shape rather than on an intent it would otherwise have to be told.
/// </summary>
public sealed class StateAgent(IChatClient chatClient, IScopedKnowledge knowledge) : IStateAgent
{
    /// <summary>
    /// The correction shape. It asks for a relationship as well as prose, because a correction that
    /// only changes what the steward would <i>say</i> has not corrected what it <i>knows</i>.
    /// </summary>
    private const string CorrectionSchema = """
        message: one or two sentences, addressed to the operator
        predicate: the relationship in capitals with underscores, or empty if the correction is not about a relationship
        object: the other entity the relationship points at, or empty
        """;

    /// <summary>The checkpoint shape. Prose only: the fleet snapshot is recorded by code.</summary>
    private const string CheckpointSchema = """
        message: one sentence confirming what was recorded
        """;

    private const float Temperature = 0.2f;

    private const string Source = "steward";

    private readonly IChatClient _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
    private readonly IScopedKnowledge _knowledge = knowledge ?? throw new ArgumentNullException(nameof(knowledge));

    private sealed class CorrectionReply
    {
        public string? Message { get; init; }

        public string? Predicate { get; init; }

        public string? Object { get; init; }
    }

    private sealed class CheckpointReply
    {
        public string? Message { get; init; }
    }

    /// <inheritdoc />
    public Task<TurnResponse> ApplyAsync(StateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request switch
        {
            StateRequest.ApplyCorrection correction => CorrectAsync(correction, cancellationToken),
            StateRequest.RecordCheckpoint checkpoint => CheckpointAsync(checkpoint, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(request), request, "unhandled state request")
        };
    }

    private async Task<TurnResponse> CorrectAsync(StateRequest.ApplyCorrection request, CancellationToken cancellationToken)
    {
        // What the graph already holds about the subject arrived on the request: the adapter
        // retrieved it before this handler ran, so the prompt below is answering "given what you
        // already believe, what changes" rather than writing into the dark.
        var system = $"""
            You are a steward for a fleet of running services. An operator is telling you that something you believe is wrong.

            Work out what should be recorded instead. If the correction is about how two services relate, name the relationship; otherwise leave the relationship fields empty rather than inventing one.

            Reply in TOON, matching this shape exactly and adding no other fields:
            {CorrectionSchema}
            """;

        var user = $"""
            subject: {request.Subject}
            believed: {request.Claim}
            corrected: {request.Correction}
            {ToonCodec.Array("alreadyKnown", request.KnownFacts)}
            """;

        List<ChatMessage> messages =
        [
            new(ChatRole.System, system),
            new(ChatRole.User, user)
        ];

        var response = await _chatClient
            .GetResponseAsync(messages, new ChatOptions { Temperature = Temperature }, cancellationToken)
            .ConfigureAwait(false);

        if (!ToonCodec.TryDecode<CorrectionReply>(response?.Text, out var reply) || string.IsNullOrWhiteSpace(reply?.Message))
        {
            return TurnResponse.Say("I could not work out what to record from that. Please put it another way.");
        }

        // The operator's own words are recorded regardless of whether a relationship comes out of
        // it. They are the correction; the relationship is an interpretation of it.
        await _knowledge
            .RememberAsync($"{request.Subject}: {request.Correction}", Source, cancellationToken)
            .ConfigureAwait(false);

        var message = reply.Message.Trim();

        if (string.IsNullOrWhiteSpace(reply.Predicate) || string.IsNullOrWhiteSpace(reply.Object))
        {
            return TurnResponse.Say(message);
        }

        var related = await _knowledge
            .RelateAsync(request.Subject, reply.Predicate.Trim(), reply.Object.Trim(), cancellationToken)
            .ConfigureAwait(false);

        // The knowledge layer keeps a fixed relationship vocabulary and refuses anything outside
        // it, returning false without writing. Saying so is the point: a steward that reported a
        // relationship it had not recorded would be wrong in exactly the way this turn was called
        // to fix.
        return TurnResponse.Say(related
            ? message
            : $"{message} I kept the note, but not the relationship: \"{reply.Predicate.Trim()}\" is not one I can record.");
    }

    private async Task<TurnResponse> CheckpointAsync(StateRequest.RecordCheckpoint request, CancellationToken cancellationToken)
    {
        var system = $"""
            You are a steward for a fleet of running services. You have just recorded a checkpoint of the fleet under the operator's label. Confirm it back to them.

            Reply in TOON, matching this shape exactly and adding no other fields:
            {CheckpointSchema}
            """;

        var user = $"""
            label: {request.Label}
            {ToonCodec.Array("fleet", request.Fleet)}
            """;

        List<ChatMessage> messages =
        [
            new(ChatRole.System, system),
            new(ChatRole.User, user)
        ];

        var response = await _chatClient
            .GetResponseAsync(messages, new ChatOptions { Temperature = Temperature }, cancellationToken)
            .ConfigureAwait(false);

        // The snapshot is written from state, not from the reply. If the model's confirmation is
        // unreadable the checkpoint still exists, because what was recorded never depended on it.
        var snapshot = $"checkpoint \"{request.Label}\": {string.Join("; ", request.Fleet)}";

        await _knowledge.RememberAsync(snapshot, Source, cancellationToken).ConfigureAwait(false);

        return ToonCodec.TryDecode<CheckpointReply>(response?.Text, out var reply) && !string.IsNullOrWhiteSpace(reply?.Message)
            ? TurnResponse.Say(reply.Message.Trim())
            : TurnResponse.Say($"Recorded a checkpoint of {request.Fleet.Count} service(s) as \"{request.Label}\".");
    }
}
