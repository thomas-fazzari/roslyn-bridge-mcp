// Copyright (C) RoslynBridge contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json.Nodes;

namespace RoslynBridge.Lsp;

internal sealed partial class RoslynSession
{
    private const int FileChangeBatchSize = 128;

    private void StartWatching()
    {
        _watcher = new FileSystemWatcher(paths.Root)
        {
            IncludeSubdirectories = true,
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

    private void OnFileChanged(object sender, FileSystemEventArgs args) => QueueFile(args.FullPath);

    private void OnFileRenamed(object sender, RenamedEventArgs args)
    {
        QueueFile(args.OldFullPath);
        QueueFile(args.FullPath);
    }

    private void OnWatcherError(object sender, ErrorEventArgs args) =>
        Interlocked.Exchange(ref _scanRequested, 1);

    internal void QueueFile(string path)
    {
        if (!paths.IsWatched(path))
        {
            return;
        }

        if (_pendingFiles.Count >= WorkspacePaths.MaximumFileCount)
        {
            Interlocked.Exchange(ref _scanRequested, 1);
            return;
        }

        _pendingFiles[path] = 0;
    }

    private async Task SynchronizeWatchedFilesAsync(CancellationToken cancellationToken)
    {
        var changedPaths = _pendingFiles
            .Keys.Where(path => _pendingFiles.TryRemove(path, out _))
            .ToList();

        var forceAllChanges = Interlocked.Exchange(ref _scanRequested, 0) != 0;
        var rescan = forceAllChanges || RequiresRescan(changedPaths);
        try
        {
            if (rescan)
            {
                var current = ScanWorkspace(cancellationToken);
                await NotifyFileChangesAsync(
                    current,
                    changedPaths.ToHashSet(StringComparer.Ordinal),
                    forceAllChanges,
                    cancellationToken
                );

                _workspaceFiles = current;
            }
            else if (changedPaths.Count > 0)
            {
                await SynchronizeChangedPathsAsync(changedPaths, cancellationToken);
            }
        }
        catch
        {
            // Retry the same paths next time instead of resending the whole workspace
            if (forceAllChanges)
            {
                Interlocked.Exchange(ref _scanRequested, 1);
            }

            foreach (var path in changedPaths)
            {
                _pendingFiles[path] = 0;
            }

            throw;
        }
    }

    /// <summary>
    /// Whether a changed path is a directory or a deleted directory with known files.
    /// </summary>
    /// <remarks>
    /// The watcher may not report the files of a directory one by one.
    /// A missing path without known files, such as an editor swap file, needs no walk.
    /// </remarks>
    private bool RequiresRescan(List<string> changedPaths)
    {
        HashSet<string>? knownDirectories = null;
        return changedPaths.Exists(path =>
            Directory.Exists(path)
            || (
                !_workspaceFiles.ContainsKey(path)
                && !File.Exists(path)
                && (knownDirectories ??= KnownDirectories()).Contains(path)
            )
        );
    }

    private HashSet<string> KnownDirectories()
    {
        var directories = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in _workspaceFiles.Keys)
        {
            // Once a directory is present, its parents are too
            var directory = Path.GetDirectoryName(file);
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

    private async Task SynchronizeChangedPathsAsync(
        List<string> changedPaths,
        CancellationToken cancellationToken
    )
    {
        var updates = new Dictionary<string, FileStamp?>(StringComparer.Ordinal);
        var changes = new List<FileChange>();
        foreach (var path in changedPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = new FileInfo(path);
            var exists = _workspaceFiles.ContainsKey(path);
            // The workspace scan skips symlinks, so a link counts as a missing file
            FileStamp? current =
                file.Exists && !file.Attributes.HasFlag(FileAttributes.ReparsePoint)
                    ? new FileStamp(file.Length, file.LastWriteTimeUtc)
                    : null;
            if (current is null)
            {
                if (exists)
                {
                    changes.Add(new FileChange(path, FileDeleted));
                }
            }
            else
            {
                // A change signal remains valid when size and timestamp are preserved
                changes.Add(new FileChange(path, exists ? FileChanged : FileCreated));
            }

            updates.Add(path, current);
        }

        await SendFileChangesAsync(changes, cancellationToken);

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
    }

    private async Task SendFileChangesAsync(
        List<FileChange> changes,
        CancellationToken cancellationToken
    )
    {
        foreach (var batch in changes.Chunk(FileChangeBatchSize))
        {
            foreach (var change in batch)
            {
                // Roslyn rereads these files, so their recorded fingerprints are no longer reliable
                _snapshotFingerprints.Remove(change.Path);
            }

            if (!_watchRegistrations.IsEmpty)
            {
                await NotifyAsync(
                    LspMethods.WorkspaceDidChangeWatchedFiles,
                    new JsonObject { ["changes"] = new JsonArray([.. batch.Select(ToFileEvent)]) },
                    cancellationToken
                );
            }

            foreach (var change in batch)
            {
                if (change.Type is not FileDeleted)
                {
                    await SynchronizeClosedDocumentAsync(change.Path, cancellationToken);
                }
            }
        }
    }

    private JsonNode ToFileEvent(FileChange change) =>
        new JsonObject { ["uri"] = new Uri(change.Path).AbsoluteUri, ["type"] = change.Type };

    private readonly record struct FileChange(string Path, int Type);
}
