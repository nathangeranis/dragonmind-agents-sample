---
name: csharp
description: C# conventions for this repository - records, sealed types, nullable wire models, and the culture-folding rules the build enforces as errors.
---

# C#

.NET 10, nullable reference types on, `ImplicitUsings` on. The conventions below are the ones the
build actually enforces, or that a reviewer here will raise.

## Shapes

- **Records for data.** Requests, results and state are `sealed record`. Value equality is what tests
  assert on: `Assert.Equal(new PlannedAction("restart", "orders-api", null), actions[0])`.
- **Closed hierarchies over nullable unions.** `IntentResult` and `StateRequest` are abstract records
  with a private constructor and nested `sealed record` cases. A new case becomes a compile-time gap
  at every `switch` instead of a null at run time.
- **`sealed` by default.** Nothing here is designed for inheritance except those closed hierarchies.
- **Collection expressions.** `[.. items.Select(...)]`, not `new List<T>(...)`.

## Nullability

Every wire type — anything decoded from model output — declares *every* property nullable, whatever
the schema requires. "The model omitted it" is the case being tested for. Declaring a property
non-nullable does not make the value arrive; it only moves the failure somewhere less obvious than
the validation that was meant to catch it.

## Culture

`CA1304` and `CA1311` are **errors** here, set in `.editorconfig`. Anything folded and then compared
against an ASCII literal must be invariant or ordinal:

```csharp
predicate.Trim().Replace(' ', '_').ToUpperInvariant();     // yes
string.Equals(a, b, StringComparison.OrdinalIgnoreCase);   // yes
text.ToUpper() == "ACTION";                                // NO - culture-sensitive
```

Measured under `tr-TR` and `az-Latn-AZ`: `"action".ToUpper()` does not equal `"ACTION"`, because both
cultures fold `i` to a dotted capital. `ToUpperInvariant` is unaffected. The failure is silent — a
classification that stops matching falls to the "could not classify" arm, which is itself a
legitimate outcome, so nothing looks broken.

**`Enum.TryParse` is not the hazard here, and the reason to avoid it is different.** Its `ignoreCase`
path is ordinal and handles a Turkish host correctly — verified, not assumed. What it also does,
measured on this target, is accept input a classifier must never be able to produce: `"0"` parses to
`Action`, `"+1"` to `StateQuery`, a comma-separated list parses to its last member, and `"99"` parses
to an **undefined** `Intent` whose value is 99 — which then matches no key in the schema table and
throws a long way from the parse. `ResponseSchema.TryReadIntent` compares against
`Enum.GetValues<Intent>()` so that only the four declared names are accepted.

## Async

`async`/`await` throughout, `ConfigureAwait(false)` in library code, `CancellationToken` last with a
`default`. `IDE0390` and `IDE0391` are errors, so an `async` method with no `await` fails the build:
return `Task.FromResult` or `Task.CompletedTask` rather than adding a meaningless await to silence it.

## Docs

XML doc comments on public types and members. `GenerateDocumentationFile` is on with `CS1591`,
`CS1573` and `CS1574` suppressed, so **malformed XML fails the build** while a missing comment does
not. A `<remarks>` block that explains why a thing is shaped the way it is earns its place; one that
restates the method name does not.

When a comment describes behaviour, name the condition the code actually branches on — not the case
you had in mind while writing it.
