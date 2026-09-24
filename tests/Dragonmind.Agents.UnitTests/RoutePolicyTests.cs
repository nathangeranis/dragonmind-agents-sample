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
    // plausible. Each hands the real RoutePolicy, through its internal constructor, the shipped
    // table broken in exactly one way and otherwise intact - so it is production Resolve that
    // runs, and the table has no other defect for it to throw on instead. Each runs for every
    // cell, because a Resolve can be wrong for some cells and right for the rest.
    //
    // They guard different mistakes. First also throws on a gap, so only the first test can tell
    // Single from First. The two gap tests are what stop a Resolve answering for a cell nobody
    // wrote a row for, whether by inventing a route or by borrowing another row's.
    // -----------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(EveryCell))]
    public void AnAmbiguousTableThrowsRatherThanPickingTheFirstMatch(Intent intent, bool windowOpen)
    {
        // One cell's row written a second time. A second row sending the turn to another handler is
        // the case that matters: First would let row position choose the route. An exact copy
        // changes no route, but it is still a table somebody got wrong, and Resolve must not
        // quietly merge it either.
        var state = new RoutingState(windowOpen);
        var row = Assert.Single(_policy.Rules, r => (r.Intent, r.Window) == (intent, state.Window));
        var otherHandler = Enum.GetValues<HandlerId>().First(h => h != row.Handler);

        var conflicting = new RoutePolicy([.. _policy.Rules, row with { Handler = otherHandler }]);
        var duplicated = new RoutePolicy([.. _policy.Rules, row]);

        Assert.Throws<InvalidOperationException>(() => conflicting.Resolve(intent, state));
        Assert.Throws<InvalidOperationException>(() => duplicated.Resolve(intent, state));
    }

    [Theory]
    [MemberData(nameof(EveryCell))]
    public void AGapInTheTableThrowsRatherThanFallingThrough(Intent intent, bool windowOpen)
    {
        // One cell's row removed and every other row left in place, so a fallback that borrowed a
        // neighbouring row - the same intent's other window, or another intent's - finds one here.
        var window = windowOpen ? ChangeWindow.Open : ChangeWindow.Closed;
        var incomplete = new RoutePolicy([.. _policy.Rules.Where(r => (r.Intent, r.Window) != (intent, window))]);

        Assert.Equal(_policy.Rules.Count - 1, incomplete.Rules.Count);
        Assert.Throws<InvalidOperationException>(
            () => incomplete.Resolve(intent, new RoutingState(windowOpen)));
    }

    [Theory]
    [MemberData(nameof(EveryCell))]
    public void AnIntentWithNoRowsThrowsRatherThanFallingThrough(Intent intent, bool windowOpen)
    {
        // A forgotten intent - how the README's "Adding a fifth intent" goes wrong - is missing both
        // rows, not one. A default route for an intent the table has no rows for never fires in the
        // test above, because there the other window's row is still present.
        var incomplete = new RoutePolicy([.. _policy.Rules.Where(r => r.Intent != intent)]);

        Assert.Equal(_policy.Rules.Count - Enum.GetValues<ChangeWindow>().Length, incomplete.Rules.Count);
        Assert.Throws<InvalidOperationException>(
            () => incomplete.Resolve(intent, new RoutingState(windowOpen)));
    }
}
