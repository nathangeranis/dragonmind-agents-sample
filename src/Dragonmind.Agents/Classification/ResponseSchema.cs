using Dragonmind.Agents.Toon;

namespace Dragonmind.Agents.Classification;

/// <summary>
/// The response shape for one intent: the worked example that teaches it, and the binding that
/// turns model output into a validated <see cref="IntentResult"/> or an explanation of why it
/// could not.
/// </summary>
/// <remarks>
/// Schemas are per intent rather than one shared shape for all of them. Two reasons, and the second
/// is the one that matters:
/// <list type="number">
/// <item>Cost. A handler call carries only the schema for the request it is making, not the union
/// of every request the system can make. The classifier is the one place that necessarily carries
/// all four, because choosing between them is its job.</item>
/// <item>Validation. A shared shape can only check that the response is well-formed. A per-intent
/// shape can check that it is well-formed <i>for the thing the model said it was</i> — that an
/// action request actually names an action, that a correction actually names a subject to look up.
/// That check is what stands between a garbled response and a change to the fleet.</item>
/// </list>
/// </remarks>
public sealed class ResponseSchema
{
    private readonly Func<string, BindResult> _bind;

    private ResponseSchema(Intent intent, string example, Func<string, BindResult> bind)
    {
        Intent = intent;
        Example = example;
        _bind = bind;
    }

    /// <summary>The intent this schema describes.</summary>
    public Intent Intent { get; }

    /// <summary>The worked example shown to the model, in the notation it must answer in.</summary>
    public string Example { get; }

    /// <summary>
    /// Binds model output to this intent's result type. Returns <see langword="false"/> with a
    /// reason when the output decodes but does not satisfy the schema — an empty action list, a
    /// correction with nothing to look up. The reason is for the operator and the log; it never
    /// carries a partial result for a caller to salvage.
    /// </summary>
    public bool TryBind(string toon, out IntentResult? result, out string? error)
    {
        var bound = _bind(toon);
        result = bound.Result;
        error = bound.Error;
        return result is not null;
    }

    private readonly record struct BindResult(IntentResult? Result, string? Error);

    private static BindResult Ok(IntentResult result) => new(result, null);

    private static BindResult Fail(string error) => new(null, error);

    // ---------------------------------------------------------------------------------------
    // Wire types. Every property is nullable regardless of what the schema requires, because
    // "the model omitted it" is exactly the case being tested for. Declaring a property
    // non-nullable would not make the value arrive; it would only move the failure to a null
    // reference somewhere less obvious than the validation below.
    // ---------------------------------------------------------------------------------------

    private sealed class IntentTag
    {
        public string? Intent { get; init; }
    }

    private sealed class ActionRow
    {
        public string? Verb { get; init; }

        public string? Target { get; init; }

        public string? Parameter { get; init; }
    }

    private sealed class ActionPayload
    {
        public List<ActionRow>? Actions { get; init; }
    }

    private sealed class QueryPayload
    {
        public string? Question { get; init; }
    }

    private sealed class CorrectionPayload
    {
        public string? Subject { get; init; }

        public string? Claim { get; init; }

        public string? Correction { get; init; }
    }

    private sealed class CheckpointPayload
    {
        public string? Label { get; init; }
    }

    /// <summary>
    /// Reads only the <c>intent</c> tag, so the correct schema can be chosen before the payload is
    /// interpreted. Decoding straight into one intent's shape would make a mislabelled response
    /// look like a malformed one.
    /// </summary>
    internal static bool TryReadIntent(string toon, out Intent intent)
    {
        intent = default;

        if (!ToonCodec.TryDecode<IntentTag>(toon, out var tag) || string.IsNullOrWhiteSpace(tag?.Intent))
        {
            return false;
        }

        // Matched against the declared names, ordinally, rather than with Enum.TryParse. The
        // reason is not culture - TryParse's ignoreCase path is ordinal and handles a Turkish host
        // correctly. It is that TryParse also accepts things a classifier must never be able to
        // say: measured on this target, "0" parses to Action, "+1" to StateQuery, a comma-separated
        // list parses to its last member, and "99" parses to an UNDEFINED Intent whose value is 99 -
        // which would then miss every key in the schema table and throw far from here. Comparing
        // against Enum.GetValues accepts the four names and nothing else.
        foreach (var candidate in Enum.GetValues<Intent>())
        {
            if (string.Equals(candidate.ToString(), tag.Intent.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                intent = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>Every schema, keyed by intent.</summary>
    public static IReadOnlyDictionary<Intent, ResponseSchema> All { get; } =
        new Dictionary<Intent, ResponseSchema>
        {
            [Intent.Action] = new(
                Intent.Action,
                """
                intent: Action
                actions[1]{verb,target,parameter}:
                  restart,orders-api,
                """,
                toon =>
                {
                    if (!ToonCodec.TryDecode<ActionPayload>(toon, out var payload))
                    {
                        return Fail("the response did not decode as an action request");
                    }

                    var rows = payload?.Actions ?? [];

                    if (rows.Count == 0)
                    {
                        return Fail("an action request named no actions");
                    }

                    var actions = new List<PlannedAction>(rows.Count);

                    foreach (var row in rows)
                    {
                        if (string.IsNullOrWhiteSpace(row.Verb) || string.IsNullOrWhiteSpace(row.Target))
                        {
                            return Fail("an action was missing its verb or its target");
                        }

                        actions.Add(new PlannedAction(
                            row.Verb.Trim(),
                            row.Target.Trim(),
                            string.IsNullOrWhiteSpace(row.Parameter) ? null : row.Parameter.Trim()));
                    }

                    return Ok(new IntentResult.ActionRequested(actions));
                }),

            [Intent.StateQuery] = new(
                Intent.StateQuery,
                """
                intent: StateQuery
                question: which services depend on payments-db
                """,
                toon =>
                {
                    if (!ToonCodec.TryDecode<QueryPayload>(toon, out var payload))
                    {
                        return Fail("the response did not decode as a state query");
                    }

                    return string.IsNullOrWhiteSpace(payload?.Question)
                        ? Fail("a state query carried no question")
                        : Ok(new IntentResult.StateQueried(payload.Question.Trim()));
                }),

            [Intent.Correction] = new(
                Intent.Correction,
                """
                intent: Correction
                subject: orders-api
                claim: orders-api depends on payments-db
                correction: orders-api no longer depends on payments-db
                """,
                toon =>
                {
                    if (!ToonCodec.TryDecode<CorrectionPayload>(toon, out var payload))
                    {
                        return Fail("the response did not decode as a correction");
                    }

                    if (string.IsNullOrWhiteSpace(payload?.Subject))
                    {
                        // Without a subject there is nothing to look up, and a correction that
                        // skips the lookup is just an unverified write.
                        return Fail("a correction named no subject to check");
                    }

                    return string.IsNullOrWhiteSpace(payload.Correction)
                        ? Fail("a correction said what was wrong but not what is right")
                        : Ok(new IntentResult.CorrectionOffered(
                            payload.Subject.Trim(),
                            (payload.Claim ?? string.Empty).Trim(),
                            payload.Correction.Trim()));
                }),

            [Intent.Checkpoint] = new(
                Intent.Checkpoint,
                """
                intent: Checkpoint
                label: before the payments rollout
                """,
                toon =>
                {
                    if (!ToonCodec.TryDecode<CheckpointPayload>(toon, out var payload))
                    {
                        return Fail("the response did not decode as a checkpoint");
                    }

                    return string.IsNullOrWhiteSpace(payload?.Label)
                        ? Fail("a checkpoint carried no label")
                        : Ok(new IntentResult.CheckpointRequested(payload.Label.Trim()));
                })
        };
}
