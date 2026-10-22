// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace RoslynBridge;

/// <summary>
/// Prints the NOTICE, the third-party notices and the .NET runtime notices embedded in the executable.
/// </summary>
internal static class Licenses
{
    internal const string Command = "licenses";

    private static readonly string[] _resources =
    [
        "NOTICE",
        "THIRD_PARTY_NOTICES.md",
        "DOTNET_RUNTIME_NOTICES.txt",
    ];

    public static void Print(TextWriter output) =>
        output.Write(string.Join('\n', _resources.Select(Read)));

    private static string Read(string name)
    {
        using var stream =
            typeof(Licenses).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"The embedded resource {name} is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
