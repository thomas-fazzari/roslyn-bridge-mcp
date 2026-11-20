// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Lsp.Protocol;

namespace RoslynBridge.Lsp;

internal sealed partial class RoslynConnection
{
    private const int FileChangeBatchSize = 128;

    // On Windows, the 8 KB default overflows on a branch switch or a restore, which forces a full rescan
    private const int WatcherBufferBytes = 64 * 1024;

    private void QueueFile(WorkspaceFilePath path)
    {
        if (!paths.IsWatched(path))
            return;

        if (_pendingFiles.Count >= WorkspacePaths.MaximumFileCount)
        {
            Interlocked.Exchange(ref _scanRequested, 1);
            return;
        }

        _pendingFiles[path] = 0;
    }

    private void StartWatching()
    {
        _watcher = new FileSystemWatcher(paths.Root)
        {
            IncludeSubdirectories = true,
            InternalBufferSize = WatcherBufferBytes,
            NotifyFilter =
                NotifyFilters.FileName
                | NotifyFilters.DirectoryName
                | NotifyFilters.LastWrite
                | NotifyFilters.Size,
        };
        _watcher.Created += OnFileChanged;
        _watcher.Changed += OnFileChanged;
        _watcher.Deleted += OnFileChanged;
        _watcher.Renamed += OnFileRenamed;
        _watcher.Error += OnWatcherError;
        _watcher.EnableRaisingEvents = true;
    }

    private void OnFileChanged(object sender, FileSystemEventArgs args) =>
        QueueFile(paths.Known(args.FullPath));

    private void OnFileRenamed(object sender, RenamedEventArgs args)
    {
        QueueFile(paths.Known(args.OldFullPath));
        QueueFile(paths.Known(args.FullPath));
    }

    private void OnWatcherError(object sender, ErrorEventArgs args) =>
        Interlocked.Exchange(ref _scanRequested, 1);

    private async Task<Result> SynchronizeWatchedFilesAsync(CancellationToken cancellationToken)
    {
        var changedPaths = _pendingFiles
            .Keys.Where(path => _pendingFiles.TryRemove(path, out _))
            .ToList();

        var scanRequested = Interlocked.Exchange(ref _scanRequested, 0) != 0;
        var rescan = scanRequested || RequiresRescan(changedPaths);
        // A failed or canceled synchronization sends the same changes again on the next call
        Result synchronized;
        try
        {
            synchronized = rescan
                ? await RescanAsync(changedPaths, cancellationToken)
                : await SynchronizeChangedPathsAsync(changedPaths, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Requeue(changedPaths, scanRequested);
            throw;
        }

        if (synchronized is ToolError)
            Requeue(changedPaths, scanRequested);

        return synchronized;
    }

    private async Task<Result> RescanAsync(
        List<WorkspaceFilePath> changedPaths,
        CancellationToken cancellationToken
    )
    {
        if (ScanWorkspace(cancellationToken).IsError(out var error, out var current))
            return error;

        var notified = await NotifyFileChangesAsync(
            current,
            changedPaths.ToHashSet(),
            cancellationToken
        );
        if (notified is not ToolError)
            _workspaceFiles = current;

        return notified;
    }

    private void Requeue(List<WorkspaceFilePath> changedPaths, bool scanRequested)
    {
        if (scanRequested)
            Interlocked.Exchange(ref _scanRequested, 1);

        foreach (var path in changedPaths)
        {
            _pendingFiles[path] = 0;
        }
    }

    /// <summary>
    /// True for a directory or a deleted directory with known files, as the watcher may not list its files.
    /// </summary>
    private bool RequiresRescan(List<WorkspaceFilePath> changedPaths)
    {
        HashSet<string>? knownDirectories = null;
        return changedPaths.Exists(path =>
            Directory.Exists(path.FullPath)
            || (
                !_workspaceFiles.ContainsKey(path)
                && !File.Exists(path.FullPath)
                && (knownDirectories ??= KnownDirectories()).Contains(path.FullPath)
            )
        );
    }

    private HashSet<string> KnownDirectories()
    {
        var directories = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in _workspaceFiles.Keys)
        {
            // Once a directory is present, its parents are too
            var directory = Path.GetDirectoryName(file.FullPath);
            while (
                directory is not null
                && directory.Length > paths.Root.Length
                && directories.Add(directory)
            )
            {
                directory = Path.GetDirectoryName(directory);
            }
        }

        return directories;
    }

    private async Task<Result> SynchronizeChangedPathsAsync(
        List<WorkspaceFilePath> changedPaths,
        CancellationToken cancellationToken
    )
    {
        if (changedPaths.Count == 0)
            return Result.Success;

        var updates = new Dictionary<WorkspaceFilePath, FileStamp?>();
        var changes = new List<FileEvent>();
        foreach (var path in changedPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var exists = _workspaceFiles.ContainsKey(path);
            var current = FileStamp.Read(path);
            if (current is null)
            {
                if (exists)
                    changes.Add(new FileEvent(path, FileEventType.Deleted));
            }
            else
            {
                // A change signal remains valid when size and timestamp are preserved
                changes.Add(
                    new FileEvent(path, exists ? FileEventType.Changed : FileEventType.Created)
                );
            }

            updates.Add(path, current);
        }

        if (await SendFileChangesAsync(changes, cancellationToken) is ToolError error)
            return error;

        foreach (var (path, stamp) in updates)
        {
            if (stamp is { } value)
            {
                _workspaceFiles[path] = value;
            }
            else
            {
                _workspaceFiles.Remove(path);
            }
        }

        return Result.Success;
    }

    private async Task<Result> SendFileChangesAsync(
        List<FileEvent> changes,
        CancellationToken cancellationToken
    )
    {
        foreach (var batch in changes.Chunk(FileChangeBatchSize))
        {
            if (callbacks.WatchesFiles)
            {
                var notified = await NotifyAsync(
                    LspMethods.WorkspaceDidChangeWatchedFiles,
                    new JsonObject
                    {
                        ["changes"] = new JsonArray([
                            .. WithProjectReloads(batch).Select(ToFileEvent),
                        ]),
                    },
                    cancellationToken
                );
                if (notified is ToolError notifyError)
                    return notifyError;
            }

            foreach (var change in batch)
            {
                if (change.Type is FileEventType.Deleted)
                {
                    _createdFiles.TryRemove(change.Path, out _);
                    continue;
                }

                if (change.Type is FileEventType.Created)
                    _createdFiles[change.Path] = 0;

                if (
                    await SynchronizeClosedDocumentAsync(change.Path, cancellationToken)
                    is ToolError error
                )
                {
                    return error;
                }
            }
        }

        return Result.Success;
    }

    // On Windows, Roslyn checks a created or deleted file against the project globs with a lowercase drive
    // letter and a case-sensitive match, so the project never reloads. A project file change always does.
    private IEnumerable<FileEvent> WithProjectReloads(FileEvent[] batch)
    {
        if (!OperatingSystem.IsWindows() || !Array.Exists(batch, IsSourceFileAddedOrRemoved))
            return batch;

        var projectsByDirectory = _workspaceFiles
            .Keys.Where(path =>
                string.Equals(
                    Path.GetExtension(path.FullPath),
                    ".csproj",
                    StringComparison.OrdinalIgnoreCase
                )
            )
            .ToLookup(
                path => Path.GetDirectoryName(path.FullPath) ?? string.Empty,
                StringComparer.Ordinal
            );
        var reloads = batch
            .Where(IsSourceFileAddedOrRemoved)
            .SelectMany(change => NearestProjects(change.Path, projectsByDirectory))
            .Distinct()
            .Select(project => new FileEvent(project, FileEventType.Changed));
        return batch.Concat(reloads);
    }

    private static bool IsSourceFileAddedOrRemoved(FileEvent change) =>
        change.Type is FileEventType.Created or FileEventType.Deleted
        && WorkspacePaths.IsCSharpFile(change.Path);

    private IEnumerable<WorkspaceFilePath> NearestProjects(
        WorkspaceFilePath path,
        ILookup<string, WorkspaceFilePath> projectsByDirectory
    )
    {
        var directory = Path.GetDirectoryName(path.FullPath);
        while (directory is not null && directory.Length >= paths.Root.Length)
        {
            if (projectsByDirectory.Contains(directory))
                return projectsByDirectory[directory];

            directory = Path.GetDirectoryName(directory);
        }

        return [];
    }

    private JsonNode ToFileEvent(FileEvent change) =>
        new JsonObject { ["uri"] = change.Path.ToUri(), ["type"] = (int)change.Type };

    private readonly record struct FileEvent(WorkspaceFilePath Path, FileEventType Type);

    // The values of the LSP FileChangeType
    private enum FileEventType
    {
        Created = 1,
        Changed = 2,
        Deleted = 3,
    }
}
