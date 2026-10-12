# C# conventions

These rules apply to every .NET project, including tests. They cover what the analyzers and formatters cannot check.

## Analyzers

`eng/Analyzers.globalconfig` sets the analyzer severities and style rules for every project. `.editorconfig` only holds editor settings. The build treats every warning as an error.

Fix the cause of a diagnostic, and change the design when the rule points at a real problem. Suppress a rule with `[SuppressMessage]` or `[UnconditionalSuppressMessage]` only when an external contract forces the code, on the narrowest member and with a `Justification`. Change a severity only when the rule is wrong for the whole repository.

Every C# file starts with the license header from `file_header_template`. The quick fix for IDE0073 adds it.

## Errors

Return an expected failure as a `ToolError` in a `Result<T>`, such as invalid tool arguments, an unknown symbol or an expired proposal. Code deep in the edit pipeline, where returning a result through every caller would hurt readability, throws a `ToolErrorException` instead. Use `invalid_request` for caller errors and `lsp_error` for invalid data from Roslyn.

Throw for failures the bridge cannot prevent, such as I/O, a lost Roslyn connection, a timeout or a cancellation. The tool boundary maps those exceptions to error codes. A broken invariant is a bug: throw `InvalidOperationException`, which the boundary does not report as a caller error.

Return a result rather than a `bool` with the value in an `out` parameter.

Catch `Exception` when every failure is handled the same way, such as the top of a background operation. Filter by type only when the type changes the outcome. Do not catch an exception only to log it and rethrow it.

## Code

- Name an argument when several arguments of the same type could be swapped.
- Keep a file under 1,000 lines. Split the type before it grows past that.
- Add a documentation comment only when it tells the caller something the signature does not. Describe return values in `<returns>`, and refer to symbols with `<see cref>`.
- Log through source-generated `[LoggerMessage]` methods placed at the end of the class. Logs go to standard error. Never log file contents or source text.
- Add `[DebuggerDisplay]` to classes whose default display hides their identity or state, with plain field or property access.
