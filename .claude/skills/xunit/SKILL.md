---
name: xunit
description: Test conventions - which suite a test belongs in, strict mocks, and the mutation check every new guard has to pass.
---

# Tests

xunit.v3 with Moq. Two projects, and which one a test belongs in is decided by what it needs.

| Project | Needs | Holds |
| --- | --- | --- |
| `Dragonmind.Agents.UnitTests` | the SDK, nothing else | everything about the pattern; `ScriptedChatClient` stands in for the model and `IScopedKnowledge` is mocked |
| `Dragonmind.Agents.IntegrationTests` | Docker, a real PostgreSQL with pgvector and Apache AGE | that the knowledge port's calls line up with what the real facade does |

## Unit tests

Name them `MethodOrScenario_Condition_ExpectedBehaviour`. Assert on the reason a thing is true, not
only that it is:

```csharp
// The assertion is on the COUNT, not on the first match. Asserting that a route comes back
// would pass just as happily with two overlapping rules - which is the failure being guarded.
var matches = _policy.Rules.Count(r => r.Intent == intent && r.Window == window);
Assert.Equal(1, matches);
```

**Use `MockBehavior.Strict` when the claim is that something was *not* called.** The off-schema tests
use strict mocks for all three handlers and for the knowledge port, so any call at all fails the
test. That is what makes "no handler ran and nothing was retrieved" a real assertion rather than a
hopeful one.

**A vacuous test is worse than a failing one.** Where a test reflects over types, assert that the set
it found is the size you expect — `Assert.Equal(3, implementations.Count)` — before asserting
anything about its contents. A reflection query that quietly matches nothing passes every assertion
you then make about it.

## Integration tests

They read `STEWARD_TEST_CONNECTION` and **throw** when it is unset. Never add a skip path: a suite
that no-ops when the database is unreachable reports green while proving nothing.

Assert through the facade *and* by querying the database directly. A facade that returned what it was
handed without persisting it would satisfy a facade-only assertion perfectly.

Use a fresh `ScopeId` per test, so a run never sees another run's rows and never leaves rows another
run could see.

## Adding a guard

A new test that protects an invariant has to be shown to work: break the invariant deliberately,
confirm the test fails with a message that names the problem, then put it back. A guard nobody has
watched fail is a guard nobody has checked — and the ones that matter most here, the boundary tests
and the routing exhaustiveness, are exactly the ones that would pass vacuously if written slightly
wrong.

## Suppressions

`xUnit1051` is off for `tests/` only, with the reasoning and the measured violation count recorded in
`.editorconfig`. Do not add a suppression without the same two things: what was measured, and why
complying is the worse trade here.
