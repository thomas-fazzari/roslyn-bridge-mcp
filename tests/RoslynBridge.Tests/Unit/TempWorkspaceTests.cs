// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace RoslynBridge.Tests.Unit;

public abstract class TempWorkspaceTests : IDisposable
{
    protected const string SourceFile = "Source.cs";
    protected const string RenamedFile = "Renamed.cs";

    protected string Root { get; } =
        Directory.CreateTempSubdirectory("roslyn-bridge-tests-").FullName;

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected void SkipOnCaseSensitiveFileSystem()
    {
        var probe = Path.Combine(Root, "CaseProbe.tmp");
        File.WriteAllText(probe, string.Empty);
        try
        {
            Assert.SkipUnless(
                File.Exists(Path.Combine(Root, "caseprobe.tmp")),
                "The file system is case-sensitive."
            );
        }
        finally
        {
            File.Delete(probe);
        }
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposing)
            return;

        Directory.Delete(Root, recursive: true);
    }
}
