<!-- HAND-AUTHORED reference for decompile_symbol P4b (do not regenerate with generate_references.py).
     Body decompiled once via `ilspycmd -t Humanizer.InflectorExtensions` from
     humanizer.core 2.14.1 (lib/net6.0/Humanizer.dll), the version in nopCommerce's graph. Stable.
     Ground truth + a checklist of plausible-but-FALSE claims for the decompile judge rubric. -->

# `Humanizer.InflectorExtensions.Pascalize(this string input)` — ground truth (NuGet, Humanizer.Core 2.14.1)

## Decompiled body

```csharp
public static string Pascalize(this string input)
{
    return Regex.Replace(input, "(?:^|_| +)(.)", (Match match) => match.Groups[1].Value.ToUpper());
}
```

## What it actually does (authoritative)

- A single `Regex.Replace`. The pattern matches the start of the string (`^`), an underscore (`_`),
  or a run of one-or-more spaces (` +`), each followed by one character `(.)`. The whole match is
  replaced by just that following character **uppercased** — so the underscore / leading-space
  separator is consumed (removed) and the next char is capitalized.
- It uppercases ONLY the boundary character. Everything else is left **unchanged** — it does not
  lowercase the remainder of a word. So `"customerID"` → `"CustomerID"` (the `ID` stays upper),
  `"FOO"` → `"FOO"`.
- Examples: `"customer_id"` → `"CustomerId"`; `"some words"` → `"SomeWords"`; `"my_long_property"`
  → `"MyLongProperty"`.
- Uppercasing uses `string.ToUpper()` — **current-culture**, not `ToUpperInvariant()`.
- Only `^`, `_`, and spaces are treated as boundaries. Hyphens, dots, and other punctuation are
  NOT split on and are left in place.
- `null` input throws `ArgumentNullException` (from `Regex.Replace`). Empty string returns `""`.

## Checklist of plausible-but-FALSE claims (each is FALSE for this method — count any the candidate asserts as a hallucination)

1. "It splits on hyphens, dots, and other separators as well as underscores." (FALSE — only `^`,
   `_`, and runs of spaces.)
2. "It lowercases the rest of each word to produce strict PascalCase." (FALSE — it only uppercases
   the boundary char; the remainder is untouched, e.g. `"customerID"` → `"CustomerID"`.)
3. "It returns an empty string (or `null`) for `null` input." (FALSE — it throws
   `ArgumentNullException`.)
4. "It uppercases with `ToUpperInvariant` / is culture-invariant." (FALSE — it uses `ToUpper()`,
   which is culture-sensitive.)
5. "It strips digits or other non-alphanumeric characters." (FALSE — only underscores and leading
   spaces are consumed.)
6. "It trims trailing whitespace." (FALSE — a trailing space has no following char to match, so it
   is left in place.)
7. "It is the inverse of `Underscore` / round-trips with it." (FALSE — Pascalize is lossy and not a
   guaranteed inverse.)
