using Dragonmind.Agents.Toon;

using Microsoft.Extensions.AI;

namespace Dragonmind.Agents.Handlers;

/// <summary>
/// Answers a question about the fleet, or tells the operator why their request is not happening.
/// </summary>
public interface IExplainerAgent
{
    /// <summary>Produces the reply for an explain route.</summary>
    Task<TurnResponse> ExplainAsync(ExplainRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// The explainer. Reads; never writes.
/// </summary>
/// <remarks>
/// It handles both questions and refusals, which is not an accident of convenience: a refusal is a
/// question the operator has not asked yet ("why not?"), and answering it needs exactly the fleet
/// context and retrieved passages an ordinary question needs. What it never does is decide whether
/// to refuse — that arrives on <see cref="ExplainRequest.Refusal"/>, already decided by the routing
/// policy, and there is no branch here that could reach a different conclusion.
/// </remarks>
public sealed class ExplainerAgent(IChatClient chatClient) : IExplainerAgent
{
    /// <summary>
    /// The response shape for this agent, and only this agent. It is one field because that is all
    /// an explanation is; carrying the action, correction and checkpoint shapes here as well would
    /// cost tokens on every single question to describe three things this call can never produce.
    /// </summary>
    private const string ResponseSchema = """
        message: one or two sentences, addressed to the operator
        """;

    private const float Temperature = 0.3f;

    private readonly IChatClient _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));

    private sealed class Reply
    {
        public string? Message { get; init; }
    }

    /// <inheritdoc />
    public async Task<TurnResponse> ExplainAsync(ExplainRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var instruction = request.Refusal is null
            ? "Answer the operator's question using only the fleet and recalled notes below. If they do not contain the answer, say so plainly rather than guessing."
            : $"The operator's request will not be carried out. Tell them so, and give this reason: {request.Refusal}. Do not suggest a way around it.";

        var system = $"""
            You are a steward for a fleet of running services. You report what is there; you never invent a service, a version or a dependency.

            {instruction}

            Reply in TOON, matching this shape exactly and adding no other fields:
            {ResponseSchema}
            """;

        var user = $"""
            question: {request.Question}
            {ToonCodec.Array("fleet", request.Fleet)}
            {ToonCodec.Array("known", request.Facts)}
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

        if (ToonCodec.TryDecode<Reply>(response?.Text, out var reply) && !string.IsNullOrWhiteSpace(reply?.Message))
        {
            return TurnResponse.Say(reply.Message.Trim());
        }

        // A refusal must survive a malformed reply. The routing policy already decided this request
        // is not happening, and losing that to a decode failure would leave the operator with an
        // error where they should have had an answer - and, worse, no statement that their request
        // was declined.
        return TurnResponse.Say(request.Refusal is null
            ? "I could not put together an answer to that. Please ask again."
            : $"That will not be carried out: {request.Refusal}.");
    }
}
