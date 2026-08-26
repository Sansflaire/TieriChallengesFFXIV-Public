# 008 — "I cannot judge this" collapsed to `false`, and `Negate` turned it into a completion

**Status:** fixed in 0.84.38.1 (present since composite conditions landed in 0.81.33.0)
**Keywords:** condition, negate, fail closed, fail open, abstain, GameStateFlag, ConditionFlag,
ConditionType, unknown kind, version gate, tri-state, bool?

---

## Symptom

None observed in the wild — found by review. That is worth stating plainly, because the failure it
would have produced is the one this plugin can least afford: a challenge completing itself for a
player who did not earn it, silently, with no way to tell it had happened.

## Root cause

`ConditionEvaluator.Raw` returned `false` in three situations that are not "this condition is
false" but "this build cannot answer the question at all":

- a `ConditionType` this build has no case for (a challenge that slipped past the version gate),
- a `GameStateFlag` with no mapping in `ToFlag`, which returns `ConditionFlag.None`,
- any exception out of the game read, caught and swallowed.

Each carried a comment saying failing closed was the right choice. It would have been, except that
`Holds` applies `ChallengeCondition.Negate` **after** `Raw` returns:

```csharp
bool raw = Raw(c, s);
return c.Negate ? !raw : raw;      // false -> true
```

So for any negated condition — "NOT mounted", "NOT in combat" — an unjudgeable condition reported
**satisfied**. The safe-looking default was inverted by the very next line.

The `ConditionFlag.None` case is the sharpest of the three, because it does not even look like an
error path: `Plugin.Condition[ConditionFlag.None]` is an ordinary array read that returns `false`
perfectly happily, so there was no throw, no log, and nothing to notice.

## Fix

`Raw` returns `bool?`, with `null` meaning "no verdict". `Holds` short-circuits on `null` **before**
`Negate` is applied, so an unjudgeable condition can only ever block.

## Lessons

- **"Fail closed" is a property of the whole expression, not of one function's return value.**
  Returning a safe-looking default is only safe while nothing downstream can invert it. The moment a
  negation, a `!`, or a De Morgan rewrite appears between the default and the decision, the default
  has quietly become fail-open.
- **Encode "no answer" as a distinct value, not as the falsy one.** `bool?` costs nothing here and
  makes the distinction impossible to lose. Two of the three sites had a comment asserting the safe
  behaviour, which is evidence that the intent was clear and the mechanism was not.
- **A lookup that returns a sentinel needs checking even when the lookup itself cannot fail.**
  `ToFlag` returning `None` is a total function with no error channel; indexing a condition array
  with it is likewise total. Nothing fails, and the wrong answer propagates.
- Generalises past this file: any predicate system with negation and any notion of "unknown" has
  this shape. Look for it wherever a `catch` returns a bool.
