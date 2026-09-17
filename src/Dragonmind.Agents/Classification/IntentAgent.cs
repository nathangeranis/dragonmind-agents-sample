using Dragonmind.Agents.Fleet;

using Microsoft.Extensions.AI;

namespace Dragonmind.Agents.Classification;

/// <summary>
/// Reads one turn and says what kind of request it is.
/// </summary>
/// <remarks>
/// The contract is deliberately narrow: an intent and that intent's payload, and nothing else. It
/// does not say who should handle the turn, and it is not given the information that would let it —
/// the change window is absent from <see cref="StateProjection"/> on purpose.
/// </remarks>
public interface IIntentAgent
{
    /// <summary>
    /// Classifies a turn, or explains why it could not. Never guesses.
    /// </summary>
    Task<IntentClassification> ClassifyAsync(
        Interaction interaction,
        StateProjection state,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// One turn of input from the operator.
/// </summary>
/// <param name="Text">What they said, verbatim.</param>
public sealed record Interaction(string Text);

/// <summary>
/// The classifier.
/// </summary>
/// <remarks>
/// <para>
/// This is the only agent that carries every response schema, because choosing between them is the
/// job. Every other agent carries one. That asymmetry is the shape of the whole design: one call
/// pays for the menu, and the calls that follow pay only for the dish.
/// </para>
/// <para>
/// Decoding happens in two steps — read the <c>intent</c> tag, then bind the payload with that
/// intent's own schema. Binding straight into a single union shape would make a mislabelled
/// response indistinguishable from a malformed one, and would let a response carrying an action
/// payload under a <c>Checkpoint</c> tag through as a valid checkpoint with an empty label.
/// </para>
/// </remarks>
public sealed class IntentAgent(IChatClient chatClient) : IIntentAgent
{
    private const float Temperature = 0.1f;

    private readonly IChatClient _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));

    /// <inheritdoc />
    public async Task<IntentClassification> ClassifyAsync(
        Interaction interaction,
        StateProjection state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(interaction);
        ArgumentNullException.ThrowIfNull(state);

        List<ChatMessage> messages =
        [
            new(ChatRole.System, BuildSystemPrompt()),
            new(ChatRole.User, $"""
                request: {interaction.Text}
                {state.Render()}
                """)
        ];

        var response = await _chatClient
            .GetResponseAsync(messages, new ChatOptions { Temperature = Temperature }, cancellationToken)
            .ConfigureAwait(false);

        var text = response?.Text;

        if (string.IsNullOrWhiteSpace(text))
        {
            return new IntentClassification.Unclassified("the classifier returned nothing");
        }

        if (!ResponseSchema.TryReadIntent(text, out var intent))
        {
            return new IntentClassification.Unclassified("the classifier did not name one of the known request kinds");
        }

        // Nothing downstream has run yet. If the payload does not satisfy this intent's schema, the
        // turn ends here with a question back to the operator - no handler is selected, no adapter
        // is built, nothing is retrieved and nothing is written. That ordering is the guarantee:
        // "off-schema output never reaches a handler" is true because the handler is chosen after
        // this line, not because each handler checks its own input.
        return ResponseSchema.All[intent].TryBind(text, out var result, out var error)
            ? new IntentClassification.Classified(result!)
            : new IntentClassification.Unclassified(error ?? "the classifier's answer did not match its own schema");
    }

    /// <summary>
    /// Builds the classification prompt: what each kind means, and the exact shape to answer in for
    /// whichever one is chosen.
    /// </summary>
    private static string BuildSystemPrompt()
    {
        var shapes = string.Join(
            "\n\n",
            ResponseSchema.All.Values.Select(s => s.Example));

        return $"""
            You are the front door of a steward for a fleet of running services. Read one request from an operator and decide which of four kinds it is. Do not carry it out, and do not say who should.

            Action       the operator wants something done to the fleet. Parse out each thing they asked for.
            StateQuery   the operator is asking a question about the fleet. Nothing changes.
            Correction   the operator is telling you that something you believe is wrong.
            Checkpoint   the operator wants the fleet as it stands recorded under a label.

            Choose exactly one. If the request fits none of them, or you cannot tell which, answer with the kind you are least unsure of only when you genuinely are - otherwise it is better to produce nothing than to guess, because a wrong kind is acted on as if it were right.

            Reply in TOON, matching exactly one of these shapes and adding no other fields:

            {shapes}
            """;
    }
}
