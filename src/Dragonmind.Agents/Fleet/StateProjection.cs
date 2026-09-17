using System.Text;

namespace Dragonmind.Agents.Fleet;

/// <summary>
/// The view of <see cref="FleetState"/> that the classifying agent is shown.
/// </summary>
/// <remarks>
/// This type exists so that "the state" and "what the model sees of the state" are two different
/// things with a named boundary between them, rather than one object that happens to get
/// serialized somewhere. Three consequences follow, and all three are the point:
/// <list type="bullet">
/// <item>The prompt cost of classification is bounded by this projection, not by however large the
/// fleet grows.</item>
/// <item>Anything omitted here is, by construction, something the classifier cannot condition on —
/// so what it is allowed to know is reviewable in one place instead of inferred from a serializer.</item>
/// <item>Adding a field to <see cref="FleetState"/> does not silently enlarge every prompt.</item>
/// </list>
/// Note what is deliberately absent: the change window. Whether changes are permitted is a routing
/// decision, made by code after classification, and showing it here would invite the model to
/// pre-empt that decision by classifying differently when the window is shut.
/// </remarks>
public sealed record StateProjection(IReadOnlyList<string> Services, IReadOnlyList<string> RecentRequests)
{
    /// <summary>How many past requests to carry. Enough to resolve "that one", not a transcript.</summary>
    private const int RecentRequestLimit = 3;

    /// <summary>
    /// Projects a fleet state down to the service names and the last few requests.
    /// </summary>
    public static StateProjection From(FleetState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        return new StateProjection(
            [.. state.Services.Select(s => $"{s.Name} {s.Version} ({Describe(s.Health)})")],
            [.. state.RecentTurns.TakeLast(RecentRequestLimit).Select(t => t.Request)]);
    }

    /// <summary>
    /// Renders the projection for inclusion in a prompt.
    /// </summary>
    public string Render()
    {
        var sb = new StringBuilder();
        sb.Append("services[").Append(Services.Count).Append("]:");
        foreach (var service in Services)
        {
            sb.Append('\n').Append("  ").Append(service);
        }

        sb.Append('\n').Append("recentRequests[").Append(RecentRequests.Count).Append("]:");
        foreach (var request in RecentRequests)
        {
            sb.Append('\n').Append("  ").Append(request);
        }

        return sb.ToString();
    }

    private static string Describe(ServiceHealth health) => health switch
    {
        ServiceHealth.Healthy => "healthy",
        ServiceHealth.Degraded => "degraded",
        ServiceHealth.Down => "down",
        _ => "unknown"
    };
}
