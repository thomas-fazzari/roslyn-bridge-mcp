// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

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
            ".git",
            ".hg",
            ".svn",
            ".jj",
            ".bzr",
            ".vs",
            ".idea",
            ".vscode",
            ".fleet",
            ".ionide",
            ".history",
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
            ".venv",
            "venv",
            "__pycache__",
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
    /// Handles a Windows drive written as c%3A, which <see cref="Uri.LocalPath"/> leaves unusable.
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
    /// Returns null for a location outside the workspace.
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
    /// Returns null for an external or generated location, which keeps its URI.
    /// </summary>
    public string? ResultFilePath(string file) =>
        file.Contains("://", StringComparison.Ordinal) ? null : Path.GetFullPath(file, Root);

    public string RelativePath(string fullPath) =>
        Relative(fullPath).Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>
    /// Matches whole trailing path segments of <paramref name="name"/>, outside excluded directories.
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

    internal static bool IsOutside(string relative) =>
        Path.IsPathRooted(relative)
        || string.Equals(relative, "..", StringComparison.Ordinal)
        || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    private FrozenSet<string> _excluded = ExcludedDirectories;

    /// <summary>
    /// Replaces the names of an earlier call. The built-in list always applies.
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

    public bool IsExcluded(string fullPath) => Components(fullPath).Any(IsExcludedDirectory);

    public bool IsWatched(string fullPath) => Components(fullPath).All(IsWatchedDirectory);

    public IEnumerable<WorkspaceFile> EnumerateFiles() =>
        EnumerateFiles(name => !IsExcludedDirectory(name));

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

    public IEnumerable<WorkspaceFile> EnumerateWatchedFiles() => EnumerateFiles(IsWatchedDirectory);

    private static bool EndsWithSegments(string relative, string suffix) =>
        relative.EndsWith(suffix, StringComparison.Ordinal)
        && (relative.Length == suffix.Length || relative[^(suffix.Length + 1)] == '/');

    private string Relative(string fullPath) => Path.GetRelativePath(Root, fullPath);

    private string[] Components(string fullPath) =>
        Relative(fullPath).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

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
    /// Returns null for a missing file or a symlink, which the workspace scan skips.
    /// </summary>
    public static FileStamp? Read(string path)
    {
        var file = new FileInfo(path);
        return file.Exists && !file.Attributes.HasFlag(FileAttributes.ReparsePoint)
            ? new FileStamp(file.Length, file.LastWriteTimeUtc)
            : null;
    }
}
