<!-- HAND-AUTHORED reference for decompile_symbol P4a (do not regenerate with generate_references.py).
     Body decompiled once via `ilspycmd -t System.IO.Path` from
     Microsoft.NETCore.App/System.Private.CoreLib.dll (.NET 10). Stable across patch versions.
     Ground truth + a checklist of plausible-but-FALSE claims for the decompile judge rubric. -->

# `Path.Combine(string path1, string path2)` — ground truth (BCL, System.Private.CoreLib)

## Decompiled body

```csharp
public static string Combine(string path1, string path2)
{
    ArgumentNullException.ThrowIfNull(path1, "path1");
    ArgumentNullException.ThrowIfNull(path2, "path2");
    return CombineInternal(path1, path2);
}

private static string CombineInternal(string first, string second)
{
    if (string.IsNullOrEmpty(first))
        return second;
    if (string.IsNullOrEmpty(second))
        return first;
    if (IsPathRooted(second.AsSpan()))
        return second;                       // <-- discards `first`
    return JoinInternal(first.AsSpan(), second.AsSpan());
}
```

## What it actually does (authoritative)

- Throws `ArgumentNullException` if **either** argument is `null`.
- If `path1` is empty, returns `path2`; if `path2` is empty, returns `path1`.
- **If `path2` is rooted/absolute (`IsPathRooted`), it returns `path2` verbatim and DISCARDS `path1`.**
  This is the classic gotcha — `Path.Combine(@"C:\a", @"\b")` returns `@"\b"`, not `@"C:\a\b"`.
- Otherwise it joins the two with a single directory separator, inserting one only when `first`
  does not already end in a separator (`JoinInternal`).
- Pure string manipulation: it does **not** touch the filesystem, does **not** check existence,
  and does **not** normalize/resolve the result (`.` and `..` segments are left intact).
- "Rooted" is platform-dependent (Windows: a drive like `C:\`, a UNC `\\server\share`, or a
  leading `\`; a drive-relative `C:foo` also counts as rooted).
- Modern .NET does **not** validate invalid path characters here (that threw on .NET Framework).

## Checklist of plausible-but-FALSE claims (each is FALSE for this method — count any the candidate asserts as a hallucination)

1. "If the second path is absolute, it is appended after the first." (FALSE — an absolute/rooted
   second path REPLACES the result; the first argument is discarded.)
2. "It returns `null` or an empty string when an argument is `null`." (FALSE — it throws
   `ArgumentNullException`.)
3. "It normalizes the path, collapsing `.` and `..` segments." (FALSE — no normalization.)
4. "It resolves the result to an absolute path / against the current working directory." (FALSE.)
5. "It checks that the path or directory exists." (FALSE — no filesystem access at all.)
6. "It always inserts a separator between the two parts, even when the first already ends in one."
   (FALSE — a separator is inserted only when needed.)
7. "It throws when the combined path contains invalid path characters." (FALSE — modern .NET does
   not validate characters here.)
