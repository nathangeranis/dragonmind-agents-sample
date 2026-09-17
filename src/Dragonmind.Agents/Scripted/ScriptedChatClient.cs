using System.Runtime.CompilerServices;

using Microsoft.Extensions.AI;

namespace Dragonmind.Agents.Scripted;

/// <summary>
/// An <see cref="IChatClient"/> that replays canned responses, so the sample runs and every unit
/// test passes with no API key and no network.
/// </summary>
/// <remarks>
/// <para>
/// One of these is registered per agent role, under the same dependency-injection keys a real
/// provider would use. Nothing in any agent knows it is talking to a fake — they resolve
/// <see cref="IChatClient"/> by key and call <c>GetResponseAsync</c>, exactly as they do against a
/// live model.
/// </para>
/// <para>
/// <b>It throws when it runs out of script.</b> Returning empty instead would be much friendlier
/// and much worse: every agent here has a fallback for an unreadable reply, so an empty response
/// produces a plausible-looking turn that exercised none of the path under test. A test would pass
/// while proving nothing. The exception names the role and how many responses were configured,
/// because "the scripted client ran dry" is otherwise diagnosed as a bug in the agent.
/// </para>
/// </remarks>
public sealed class ScriptedChatClient : IChatClient
{
    private readonly string _role;
    private readonly IReadOnlyList<string> _responses;
    private readonly List<IReadOnlyList<ChatMessage>> _calls = [];
    private readonly Lock _gate = new();

    private int _next;

    /// <summary>
    /// Creates a client that returns <paramref name="responses"/> in order, one per call.
    /// </summary>
    /// <param name="role">The agent role this client stands in for, used in diagnostics.</param>
    /// <param name="responses">The replies, in the order they will be handed out.</param>
    public ScriptedChatClient(string role, params string[] responses)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        ArgumentNullException.ThrowIfNull(responses);

        _role = role;
        _responses = responses;
    }

    /// <summary>
    /// Every set of messages this client was called with, in order. Lets a test assert on what an
    /// agent actually sent - that a handler's prompt carried its own schema and not the others,
    /// for instance - rather than only on what came back.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<ChatMessage>> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    /// <summary>How many times this client has been asked for a response.</summary>
    public int CallCount
    {
        get
        {
            lock (_gate)
            {
                return _calls.Count;
            }
        }
    }

    /// <inheritdoc />
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var reply = Next(messages);

        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var reply = Next(messages);

        await Task.CompletedTask.ConfigureAwait(false);

        yield return new ChatResponseUpdate(ChatRole.Assistant, reply);
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Nothing to release: there is no connection, which is the point.
    }

    private string Next(IEnumerable<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        lock (_gate)
        {
            _calls.Add([.. messages]);

            if (_next >= _responses.Count)
            {
                throw new InvalidOperationException(
                    $"The scripted client for the '{_role}' role was called {_calls.Count} time(s) but only " +
                    $"{_responses.Count} response(s) were configured. Add the missing response rather than " +
                    "letting the call return nothing: every agent here falls back gracefully on an unreadable " +
                    "reply, so an empty one would produce a turn that looks fine and tested nothing.");
            }

            return _responses[_next++];
        }
    }
}
