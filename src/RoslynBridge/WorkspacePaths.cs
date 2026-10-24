// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Collections.Frozen;
using System.Diagnostics;
using System.IO.Enumeration;

namespace RoslynBridge;

[DebuggerDisplay("{Root}")]
internal sealed class WorkspacePaths(string root)
{
    internal const int MaximumFileCount = 100_000;
    internal const string IntermediateDirectoryName = "obj";
    private const int ProjectSearchDepth = 2;

    internal static FrozenSet<string> ExcludedDirectories { get; } =
        new[]
        {
            // VCS
            ".git",
            ".hg",
            ".svn",
            ".jj",
            ".bzr",
            // Editors / IDE
            ".vs",
            ".idea",
            ".vscode",
            ".fleet",
            ".ionide",
            ".history",
            // .NET / build
            "bin",
            IntermediateDirectoryName,
            "packages",
            "publish",
            "artifacts",
            "TestResults",
            "BenchmarkDotNet.Artifacts",
            ".sonarqube",
            ".sonarlint",
            ".cache",
            // Full-stack
            "node_modules",
            ".next",
            ".nuxt",
            ".svelte-kit",
            ".angular",
            ".astro",
            ".docusaurus",
            ".parcel-cache",
            ".turbo",
            ".pnpm-store",
            ".vercel",
            ".serverless",
            "dist",
            "build",
            "out",
            "coverage",
            // Python (e.g. tooling/scripts)
            ".venv",
            "venv",
            "__pycache__",
            // Rust (e.g. tooling)
            "target",
        }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> _projectExtensions = new[]
    {
        ".sln",
        ".slnx",
        ".slnf",
        ".csproj",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public string Root { get; } = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    public string RootUri => new Uri(Root + Path.DirectorySeparatorChar).AbsoluteUri;

    public string Resolve(string file)
    {
        // Path APIs reject a null character with an ArgumentException, which would read as a bridge bug
        if (string.IsNullOrWhiteSpace(file) || file.Contains('\0', StringComparison.Ordinal))
        {
            throw new ToolErrorException(
                ToolError.InvalidRequest("Provide a valid workspace file path.")
            );
        }

        var local =
            Uri.TryCreate(file, UriKind.Absolute, out var uri) && uri.IsFile
                ? LocalPath(uri)
                : file;
        var fullPath = Path.GetFullPath(local, Root);
        var relative = Relative(fullPath);
        if (IsOutside(relative))
        {
            throw new ToolErrorException(
                ToolError.InvalidRequest("The path must be inside the workspace.")
            );
        }

        var current = Root;
        foreach (var part in relative.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            FileSystemInfo entry = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : new FileInfo(current);
            if (entry.LinkTarget is not null)
            {
                throw new ToolErrorException(
                    ToolError.InvalidRequest(
                        "Symbolic links inside the workspace are not supported."
                    )
                );
            }
        }

        return fullPath;
    }

    public string ToUri(string file) => new Uri(Resolve(file)).AbsoluteUri;

    /// <summary>
    /// Returns the local path of a file URI, including a Windows drive written as c%3A, which
    /// <see cref="Uri.LocalPath"/> leaves unusable.
    /// </summary>
    public static string LocalPath(Uri uri)
    {
        if (!OperatingSystem.IsWindows() || !string.IsNullOrEmpty(uri.Host))
        {
            return uri.LocalPath;
        }

        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        // Full paths are dictionary keys, so the drive letter must match Path.GetFullPath
        return path.Length > 2 && path[0] == '/' && path[2] == ':'
            ? char.ToUpperInvariant(path[1]) + path[2..].Replace('/', Path.DirectorySeparatorChar)
            : uri.LocalPath;
    }

    /// <summary>
    /// Returns the full path of a file URI inside the workspace, or null for any other location.
    /// </summary>
    public string? TryGetWorkspaceFile(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var location) || !location.IsFile)
        {
            return null;
        }

        var fullPath = Path.GetFullPath(LocalPath(location));
        return IsOutside(Relative(fullPath)) ? null : fullPath;
    }

    /// <summary>
    /// Returns the full path of a file reported in a tool result, which is workspace-relative,
    /// or null for an external or generated location, which keeps its URI.
    /// </summary>
    public string? ResultFilePath(string file) =>
        file.Contains("://", StringComparison.Ordinal) ? null : Path.GetFullPath(file, Root);

    public string RelativePath(string fullPath) =>
        Relative(fullPath).Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>
    /// Returns the workspace-relative paths of the files outside excluded directories whose trailing
    /// path segments are <paramref name="name"/>, in ordinal order.
    /// </summary>
    public List<string> RelativePathsEndingWith(IEnumerable<string> files, string name)
    {
        var suffix = name.Replace('\\', '/');
        var fileName = Path.GetFileName(suffix);
        return
        [
            .. files
                .Where(file =>
                    file.EndsWith(fileName, StringComparison.Ordinal) && !IsExcluded(file)
                )
                .Select(RelativePath)
                .Where(relative => EndsWithSegments(relative, suffix))
                .Order(StringComparer.Ordinal),
        ];
    }

    private static bool EndsWithSegments(string relative, string suffix) =>
        relative.EndsWith(suffix, StringComparison.Ordinal)
        && (relative.Length == suffix.Length || relative[^(suffix.Length + 1)] == '/');

    internal static bool IsOutside(string relative) =>
        Path.IsPathRooted(relative)
        || string.Equals(relative, "..", StringComparison.Ordinal)
        || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    private FrozenSet<string> _excluded = ExcludedDirectories;

    /// <summary>
    /// Adds folder names to the built-in excluded list, replacing the names of an earlier call.
    /// </summary>
    public void Exclude(IEnumerable<string> names) =>
        Volatile.Write(
            ref _excluded,
            names.Concat(ExcludedDirectories).ToFrozenSet(StringComparer.Ordinal)
        );

    public bool IsExcludedDirectory(string name) => Volatile.Read(ref _excluded).Contains(name);

    // Restore changes under obj must still reach Roslyn's file watchers
    public bool IsWatchedDirectory(string name) =>
        string.Equals(name, IntermediateDirectoryName, StringComparison.Ordinal)
        || !IsExcludedDirectory(name);

    /// <summary>Returns whether any component of a workspace path is an excluded directory name.</summary>
    public bool IsExcluded(string fullPath) => Components(fullPath).Any(IsExcludedDirectory);

    /// <summary>Returns whether Roslyn must see changes to a workspace path.</summary>
    public bool IsWatched(string fullPath) => Components(fullPath).All(IsWatchedDirectory);

    private string Relative(string fullPath) => Path.GetRelativePath(Root, fullPath);

    private string[] Components(string fullPath) =>
        Relative(fullPath).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>Enumerates the files of the workspace, outside excluded directories.</summary>
    public IEnumerable<WorkspaceFile> EnumerateFiles() =>
        EnumerateFiles(name => !IsExcludedDirectory(name));

    /// <summary>Returns whether the root or a folder up to two levels below holds a solution or project file.</summary>
    public bool ContainsProject()
    {
        try
        {
            return Files(name => !IsExcludedDirectory(name), ProjectSearchDepth)
                .Any(file => _projectExtensions.Contains(Path.GetExtension(file.Path)));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Enumerates the files whose changes Roslyn must see, including those under obj.</summary>
    public IEnumerable<WorkspaceFile> EnumerateWatchedFiles() => EnumerateFiles(IsWatchedDirectory);

    // Symlinks and unreadable directories are skipped
    private FileSystemEnumerable<WorkspaceFile> Files(
        Func<string, bool> includeDirectory,
        int maximumDepth = int.MaxValue
    ) =>
        new(
            Root,
            static (ref entry) =>
                new WorkspaceFile(
                    entry.ToFullPath(),
                    entry.Length,
                    entry.LastWriteTimeUtc.UtcDateTime
                ),
            new EnumerationOptions
            {
                RecurseSubdirectories = true,
                MaxRecursionDepth = maximumDepth,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = true,
            }
        )
        {
            ShouldIncludePredicate = static (ref entry) => !entry.IsDirectory,
            ShouldRecursePredicate = (ref entry) => includeDirectory(entry.FileName.ToString()),
        };

    private IEnumerable<WorkspaceFile> EnumerateFiles(Func<string, bool> includeDirectory)
    {
        var count = 0;
        foreach (var file in Files(includeDirectory))
        {
            if (++count > MaximumFileCount)
            {
                throw new ToolErrorException(
                    ToolError.InvalidRequest(
                        $"The workspace exceeds {MaximumFileCount} files. Select a narrower root."
                    )
                );
            }

            yield return file;
        }
    }
}

internal readonly record struct WorkspaceFile(string Path, long Length, DateTime LastWriteTimeUtc)
{
    public FileStamp Stamp => new(Length, LastWriteTimeUtc);
}

internal readonly record struct FileStamp(long Length, DateTime ModifiedUtc)
{
    /// <summary>
    /// Returns the stamp of a regular file, or null for a missing file or a symlink, which the workspace scan skips.
    /// </summary>
    public static FileStamp? Read(string path)
    {
        var file = new FileInfo(path);
        return file.Exists && !file.Attributes.HasFlag(FileAttributes.ReparsePoint)
            ? new FileStamp(file.Length, file.LastWriteTimeUtc)
            : null;
    }
}
