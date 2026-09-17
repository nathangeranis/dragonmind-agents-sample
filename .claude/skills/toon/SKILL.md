---
name: toon
description: The TOON structured-output format - array header syntax, per-intent schemas, and the encode/decode asymmetry that loses data silently.
---

# TOON

[TOON](https://github.com/toon-format/toon) is the notation every agent here answers in. It declares
an array's length in the header and writes rows without repeating field names, which is where the
token saving comes from.

```
intent: Action
actions[2]{verb,target,parameter}:
  restart,orders-api,
  deploy,payments-db,4.2.0
```

The count in `[2]` is part of the contract, not decoration. A tabular block names its columns once in
`{...}` and then writes bare rows.

## The asymmetry that bites

`Toon.Encode` takes **no** serializer options and always emits camelCase. `Toon.Decode` takes a
`JsonSerializerOptions` and will happily be configured to expect something else.

When the two disagree, nothing throws. An unmatched property keeps its default — a list comes back
empty, a string comes back null, and nothing is logged. **Single-word properties round-trip under
almost any naming policy**, which is precisely what lets the bug survive a casual test.

`ToonCodec` pins camelCase on the decode side to match the encoder, and `ToonCodecTests` round-trips
a deliberately multi-word property to keep them in agreement. Do not change one side alone.

## Cleaning

`ToonCodec.Clean` strips markdown fences and trailing `#` commentary. The comment strip is
quote-aware: a naive strip from the first `#` turns `label: "release #4"` into an unterminated
string, making the cleaner the sole cause of the failure it exists to prevent.

## Per-intent schemas

Each intent owns a `ResponseSchema`: the worked example shown to the model, and the binding that
validates the decoded payload. Two rules:

1. **An agent carries only its own schema.** The explainer's prompt describes one field, because that
   is all an explanation is. Only the classifier carries all four, because choosing between them is
   its job. Adding the others to a handler's prompt spends tokens on every call describing shapes
   that call can never produce.
2. **The example must satisfy its own schema.** `ResponseSchemaTests` asserts this for all four. An
   example that does not bind is asking the model for something the decoder cannot accept, and no
   amount of prompt tuning will fix it.

Validation is the point at which a garbled answer is stopped, so it checks meaning and not only
shape. An action request naming no action, a correction with no subject to look up, and a checkpoint
with no label are all well-formed TOON, and all three are refused.

## Adding an intent

The checklist lives in the README, under "Adding a fifth intent" — kept in one place so the two
cannot drift. The part that belongs here: the new intent needs its own `ResponseSchema` entry, and
that entry's worked example must satisfy its own binding, which `ResponseSchemaTests` asserts for
every intent.
