# Roslyn Bridge test guidelines

- For a bug fix, add a regression test that fails before the fix.
- Keep one test per distinct failure mode, at the most stable boundary. Never test the same behavior at two levels.
- Move a fake or a payload builder to `Fakes` when a second test file needs it, and name it after its subject. No general `TestExtensions` class.
- Describe a scenario with a small table, as `FakeSymbolServer.With` does, instead of a `switch` over LSP methods.
- Assert on a call that throws through a local function at the end of the test, after an explicit `return;`.
- Integration tests run the real Roslyn server on temporary copies of the fixtures. Never run them against a real project.
