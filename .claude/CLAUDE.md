# CLAUDE.md — dragonmind-agents-sample

Instructions for AI assistants working in this repository. Behavioural rules only; the README
carries the design argument and is worth reading first.

## What this repository is

One pattern, small enough to read in twenty minutes: a model classifies a turn, deterministic code
routes it to exactly one handling agent, and a typed adapter builds that agent's input. The domain —
a steward for a fleet of simulated services — exists to carry the pattern and nothing else.

Everything here serves that. A change that makes the code more capable but less readable is the
wrong trade.

## Hard rules

1. **The classifier never names a handler.** No type reachable from `IntentResult` may expose a
   `HandlerId`, a `Route`, or a `RouteRule`. `BoundaryTests` enforces this. If a change needs the
   model to pick its own target, the change is wrong, not the test.
2. **Routing is a table, resolved with `Single`.** Never `First`, never a fallback arm. An ambiguous
   or incomplete table must throw at the point it is wrong. Every `intent × change window` cell is a
   row somebody wrote; the `InEitherWindow` helper expands to two rows and never participates in
   matching.
3. **Validate before routing.** An answer that fails its own intent's schema ends the turn before a
   route is resolved, before an adapter runs, and before anything is retrieved or written. There is
   no default intent.
4. **One adapter per handler.** A handler receives its own request type and nothing else. Do not
   introduce a shared context object, and do not let a handler reach `FleetState`.
5. **`Dragonmind.Agents` must never reference `Dragonmind.Knowledge`.** The library sees the facade
   contract in `Dragonmind.Core` and stops there. Persistence types belong to the composition root
   and the integration tests.
6. **No new model-provider dependency outside `Dragonmind.Agents.Sample`.** Agents consume
   `IChatClient` resolved by role key and must not learn what is behind it.
7. **Nothing key-shaped, ever.** No API key, connection string with a password, or provider name in
   source, tests, comments or fixtures. The database container uses trust auth on `127.0.0.1`.

## Verification

Nothing is done until these pass:

```bash
dotnet build Dragonmind.Agents.sln -c Release -warnaserror   # zero warnings
dotnet test tests/Dragonmind.Agents.UnitTests                # no Docker, no network, no key
dotnet run --project src/Dragonmind.Agents.Sample            # the walkthrough, unconfigured
```

And for anything touching the knowledge layer:

```bash
docker compose up -d --wait db
export STEWARD_TEST_CONNECTION="Host=localhost;Port=5456;Database=steward;Username=steward"
dotnet test tests/Dragonmind.Agents.IntegrationTests
```

Integration tests have no skip path by design. If the database is unreachable they must fail.

## When you change the walkthrough

`Walkthrough.Turns` and the README transcript are coupled. The transcript is presented as captured
output, so after any change to the walkthrough, the scripted replies, or the console host, re-run the
sample and paste the real output back into the README. Do not hand-edit it to match what you expect —
that is precisely the claim the section makes and the one thing that would make it a lie.

`Walkthrough.BuildScript` works out which handler receives each reply. If that calculation and
`RoutePolicy` ever disagree, a scripted client runs dry and throws. That is intended: fix the
disagreement rather than padding the script.

## Conventions

- XML doc comments on public types and members. `GenerateDocumentationFile` is on, so malformed XML
  fails the build; missing docs do not.
- Comments explain *why*, and name the branch condition the code actually tests rather than the case
  you had in mind.
- Culture-sensitive folding is a build error (CA1304/CA1311). Use `ToUpperInvariant`/`OrdinalIgnoreCase`
  for anything compared against a literal.
- Records for data, `sealed` by default, collection expressions over `new List<T>`.
- Tests are named `MethodOrScenario_Condition_ExpectedBehaviour` and assert on the reason a thing is
  true, not only that it is.
