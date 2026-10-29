# Ranking

The scoring below is implemented in `src/Glide.Core/Search` and locked by tests
in `tests/Glide.Core.Tests`. Change a constant only together with the tests that
describe the resulting order.

## Query terms

`QueryParser` lowercases the input, splits on whitespace, and drops repeated
terms. An empty query matches everything with score 0 and keeps input order,
which lets the caller show recent items.

## Per-term match

`FuzzyMatcher.Score(pattern, candidate)` returns null when the pattern is not a
subsequence of the candidate. Otherwise it walks the pattern left to right and
takes the first available character, which keeps the scorer linear and
deterministic.

| Event | Points |
|---|---:|
| Matched character | +1 |
| Consecutive with previous character | +25 |
| Word start (index 0, after a non-alphanumeric, or camelCase boundary) | +20 |
| Exact case match | +1 |
| Gap before the character | -2 per skipped char, capped at 6 chars |
| Match ends at the last character | +10 |

A word start is not awarded for consecutive characters, so a run is worth more
than a boundary alone.

## Entry score

For each term the engine tries the file name first, then the full path:

- name match: `1000 + nameScore`
- path-only match: `pathScore - 50`
- no match in either: the entry is dropped

Totals add up across terms. The 1000 tier guarantees that any name match outranks
any path-only match, which is why searching `budget` finds `docs/budget.xlsx`
above `budget/salary.xlsx`.

## Ordering

1. Score, descending.
2. File name length, ascending.
3. Path, ordinal ascending.

The list is bounded while scanning, so memory does not grow with the number of
candidates.
