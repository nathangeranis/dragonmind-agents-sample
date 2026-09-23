using Dragonmind.Agents.Classification;
using Dragonmind.Agents.Routing;

namespace Dragonmind.Agents.UnitTests;

/// <summary>
/// Proves the routing policy is a total function: every reachable (intent, state) resolves, and
/// resolves to exactly one handler.
/// </summary>
public class RoutePolicyTests
{
    private readonly RoutePolicy _policy = new();

    /// <summary>Every reachable combination — four intents times two window states.</summary>
    public static TheoryData<Intent, bool> EveryCell()
    {
        var data = new TheoryData<Intent, bool>();

        foreach (var intent in Enum.GetValues<Intent>())
        {
            data.Add(intent, true);
            data.Add(intent, false);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryCell))]
    public void EveryReachableCombinationMatchesExactlyOneRule(Intent intent, bool windowOpen)
    {
        // The assertion is on the COUNT, not on the first match. Asserting that a route comes back
        // would pass just as happily with two overlapping rules, which is the failure this exists
        // to catch: the route would still resolve, to whichever rule happened to be written first.
        var window = windowOpen ? ChangeWindow.Open : ChangeWindow.Closed;

        var matches = _policy.Rules.Count(r => r.Intent == intent && r.Window == window);

        Assert.Equal(1, matches);
    }

    [Theory]
    [MemberData(nameof(EveryCell))]
    public void EveryReachableCombinationResolves(Intent intent, bool windowOpen)
    {
        var route = _policy.Resolve(intent, new RoutingState(windowOpen));

        Assert.True(Enum.IsDefined(route.Handler));
    }

    [Fact]
    public void TheTableCoversTheWholeGridAndNothingElse()
    {
        // Guards the other direction: a rule for a combination that cannot occur is dead weight
        // that still has to be read and understood by whoever comes next.
        var expected = Enum.GetValues<Intent>().Length * Enum.GetValues<ChangeWindow>().Length;

        Assert.Equal(expected, _policy.Rules.Count);
        Assert.Equal(expected, _policy.Rules.Select(r => (r.Intent, r.Window)).Distinct().Count());
    }

    [Fact]
    public void AnActionRoutesToPolicyWhenTheWindowIsOpen()
    {
        var route = _policy.Resolve(Intent.Action, new RoutingState(ChangeWindowOpen: true));

        Assert.Equal(HandlerId.Policy, route.Handler);
        Assert.False(route.IsRefusal);
    }

    [Fact]
    public void TheSameActionRoutesToTheExplainerWithARefusalWhenTheWindowIsClosed()
    {
        // The pair above and here is the sample's central claim, stated as a test: the classifier
        // produced the identical intent both times, and the destination still differs. Nothing the
        // model emitted could have decided this, because the window was never in its projection.
        var route = _policy.Resolve(Intent.Action, new RoutingState(ChangeWindowOpen: false));

        Assert.Equal(HandlerId.Explainer, route.Handler);
        Assert.True(route.IsRefusal);
        Assert.Equal(RoutePolicy.ChangeWindowClosed, route.RefusalReason);
    }

    [Theory]
    [InlineData(Intent.StateQuery, HandlerId.Explainer)]
    [InlineData(Intent.Correction, HandlerId.State)]
    [InlineData(Intent.Checkpoint, HandlerId.State)]
    public void EveryOtherIntentRoutesTheSameWayInBothWindowStates(Intent intent, HandlerId expected)
    {
        var open = _policy.Resolve(intent, new RoutingState(ChangeWindowOpen: true));
        var closed = _policy.Resolve(intent, new RoutingState(ChangeWindowOpen: false));

        Assert.Equal(expected, open.Handler);
        Assert.Equal(expected, closed.Handler);
        Assert.False(open.IsRefusal);
        Assert.False(closed.IsRefusal);
    }

    [Fact]
    public void NoRuleCarriesAHandlerOutsideTheEnum()
    {
        Assert.All(_policy.Rules, r => Assert.True(Enum.IsDefined(r.Handler)));
    }

    // -----------------------------------------------------------------------------------------
    // The guard itself. Testing today's table proves today's table; these prove that a FUTURE
    // table which is ambiguous or incomplete fails loudly rather than resolving to something
    // plausible. Both hand a deliberately broken table to the real RoutePolicy through its
    // internal constructor, so it is production Resolve that runs - a test-local copy of the
    // lookup would only ever prove that the copy used Single.
    //
    // They catch different mutations. Change Single to First and the ambiguous table resolves to
    // whichever row was written first, so the first test fails. First also throws on a gap, so
    // the second test is the guard against an ...OrDefault lookup or a fallback arm instead.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void AnAmbiguousTableThrowsRatherThanPickingTheFirstMatch()
    {
        // Same handler twice, differing only in the refusal. Row order would then decide whether a
        // closed-window action is refused at all, and a Resolve that only objected to rows naming
        // different handlers would let it.
        var ambiguous = new RoutePolicy(
        [
            new RouteRule(Intent.Action, ChangeWindow.Closed, HandlerId.Explainer, RoutePolicy.ChangeWindowClosed),
            new RouteRule(Intent.Action, ChangeWindow.Closed, HandlerId.Explainer)
        ]);

        Assert.Throws<InvalidOperationException>(
            () => ambiguous.Resolve(Intent.Action, new RoutingState(ChangeWindowOpen: false)));
    }

    [Fact]
    public void AGapInTheTableThrowsRatherThanFallingThrough()
    {
        var incomplete = new RoutePolicy(
        [
            new RouteRule(Intent.Correction, ChangeWindow.Open, HandlerId.State)
        ]);

        Assert.Throws<InvalidOperationException>(
            () => incomplete.Resolve(Intent.Correction, new RoutingState(ChangeWindowOpen: false)));
    }
}
