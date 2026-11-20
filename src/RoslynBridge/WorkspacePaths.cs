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

    public static bool IsCSharpFile(string path) =>
        path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".csx", StringComparison.OrdinalIgnoreCase);

    public static bool IsCSharpFile(WorkspaceFilePath file) => IsCSharpFile(file.FullPath);

    public static bool IsExistingCSharpFile(WorkspaceFilePath file) =>
        IsCSharpFile(file) && File.Exists(file.FullPath);

    public static string FileUri(string fullPath) => new Uri(fullPath).AbsoluteUri;

    public string RootUri => FileUri(Root + Path.DirectorySeparatorChar);

    /// <summary>
    /// Turns a workspace-relative path, a full path or a file URI from a client or from Roslyn into a workspace file.
    /// </summary>
    public Result<WorkspaceFilePath> Resolve(string file)
    {
        // Path APIs reject a null character with an ArgumentException, which would read as a bridge bug
        if (string.IsNullOrWhiteSpace(file) || file.Contains('\0', StringComparison.Ordinal))
            return ToolError.InvalidRequest("Provide a valid workspace file path.");

        var local =
            Uri.TryCreate(file, UriKind.Absolute, out var uri) && uri.IsFile
                ? LocalPath(uri)
                : file;
        var fullPath = Path.GetFullPath(local, Root);
        var relative = Relative(fullPath);
        if (IsOutside(relative))
            return ToolError.InvalidRequest("The path must be inside the workspace.");

        var current = Root;
        foreach (var part in relative.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            FileSystemInfo entry = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : new FileInfo(current);
            if (entry.LinkTarget is not null)
            {
                return ToolError.InvalidRequest(
                    "Symbolic links inside the workspace are not supported."
                );
            }
        }

        return new WorkspaceFilePath(fullPath, RelativePath(fullPath));
    }

    public Result<string> ToUri(string file) => Resolve(file).Map(path => path.ToUri());

    /// <summary>
    /// Checks a file again before the bridge opens or writes it, as a symbolic link may have appeared since it was resolved.
    /// </summary>
    public Result Recheck(WorkspaceFilePath file) =>
        Resolve(file.FullPath) is ToolError error ? error : Result.Success;

    /// <summary>
    /// Handles a Windows drive written as c%3A, which <see cref="Uri.LocalPath"/> leaves unusable.
    /// </summary>
    public static string LocalPath(Uri uri)
    {
        if (!OperatingSystem.IsWindows() || !string.IsNullOrEmpty(uri.Host))
            return uri.LocalPath;

        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        // Full paths are dictionary keys, so the drive letter must match Path.GetFullPath
        return path.Length > 2 && path[0] == '/' && path[2] == ':'
            ? char.ToUpperInvariant(path[1]) + path[2..].Replace('/', Path.DirectorySeparatorChar)
            : uri.LocalPath;
    }

    /// <summary>
    /// Returns null for a location outside the workspace or behind a symbolic link.
    /// </summary>
    public WorkspaceFilePath? TryGetWorkspaceFile(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var location) || !location.IsFile)
            return null;

        return Resolve(LocalPath(location)).IsError(out _, out var file) ? null : file;
    }

    /// <summary>
    /// Wraps a path without the checks of <see cref="Resolve"/>.
    /// </summary>
    /// <remarks>
    /// For a path inside the workspace that the bridge or the file watcher produced, such as one of an edit receipt.
    /// </remarks>
    public WorkspaceFilePath Known(string path)
    {
        var fullPath = Path.GetFullPath(path, Root);
        return new WorkspaceFilePath(fullPath, RelativePath(fullPath));
    }

    public string RelativePath(string fullPath) =>
        Relative(fullPath).Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>
    /// Matches whole trailing path segments of <paramref name="name"/>, outside excluded directories.
    /// </summary>
    public List<string> RelativePathsEndingWith(IEnumerable<WorkspaceFilePath> files, string name)
    {
        var suffix = name.Replace('\\', '/');
        var fileName = Path.GetFileName(suffix);
        return
        [
            .. files
                .Where(file =>
                    file.FullPath.EndsWith(fileName, StringComparison.Ordinal) && !IsExcluded(file)
                )
                .Select(file => file.RelativePath)
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

    public bool IsExcluded(WorkspaceFilePath file) => Components(file).Any(IsExcludedDirectory);

    public bool IsWatched(WorkspaceFilePath file) => Components(file).All(IsWatchedDirectory);

    public Result<List<WorkspaceFile>> EnumerateFiles(CancellationToken cancellationToken) =>
        EnumerateFiles(name => !IsExcludedDirectory(name), cancellationToken);

    public bool ContainsProject()
    {
        try
        {
            return Files(name => !IsExcludedDirectory(name), ProjectSearchDepth)
                .Any(file => _projectExtensions.Contains(Path.GetExtension(file.Path.FullPath)));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public Result<List<WorkspaceFile>> EnumerateWatchedFiles(CancellationToken cancellationToken) =>
        EnumerateFiles(IsWatchedDirectory, cancellationToken);

    private static bool EndsWithSegments(string relative, string suffix) =>
        relative.EndsWith(suffix, StringComparison.Ordinal)
        && (relative.Length == suffix.Length || relative[^(suffix.Length + 1)] == '/');

    private string Relative(string fullPath) => Path.GetRelativePath(Root, fullPath);

    private static string[] Components(WorkspaceFilePath file) => file.RelativePath.Split('/');

    // The walk skips symbolic links and unreadable directories, so its files need no link check
    private FileSystemEnumerable<WorkspaceFile> Files(
        Func<string, bool> includeDirectory,
        int maximumDepth = int.MaxValue
    ) =>
        new(
            Root,
            (ref entry) =>
            {
                var fullPath = entry.ToFullPath();
                return new WorkspaceFile(
                    new WorkspaceFilePath(fullPath, RelativePath(fullPath)),
                    entry.Length,
                    entry.LastWriteTimeUtc.UtcDateTime
                );
            },
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

    private Result<List<WorkspaceFile>> EnumerateFiles(
        Func<string, bool> includeDirectory,
        CancellationToken cancellationToken
    )
    {
        List<WorkspaceFile> files = [];
        try
        {
            foreach (var file in Files(includeDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (files.Count == MaximumFileCount)
                {
                    return ToolError.InvalidRequest(
                        $"The workspace exceeds {MaximumFileCount} files. Select a narrower root."
                    );
                }

                files.Add(file);
            }
        }
        catch (Exception exception) when (ToolError.IsIo(exception))
        {
            return ToolError.Io(exception.Message);
        }

        return files;
    }
}

internal readonly record struct WorkspaceFile(
    WorkspaceFilePath Path,
    long Length,
    DateTime LastWriteTimeUtc
)
{
    public FileStamp Stamp => new(Length, LastWriteTimeUtc);
}

internal readonly record struct FileStamp(long Length, DateTime ModifiedUtc)
{
    /// <summary>
    /// Returns null for a missing file or a symlink, which the workspace scan skips.
    /// </summary>
    public static FileStamp? Read(WorkspaceFilePath path)
    {
        var file = new FileInfo(path.FullPath);
        return file.Exists && !file.Attributes.HasFlag(FileAttributes.ReparsePoint)
            ? new FileStamp(file.Length, file.LastWriteTimeUtc)
            : null;
    }
}
