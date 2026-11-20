// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Lsp.Protocol;
using RoslynBridge.Telemetry;

namespace RoslynBridge.Lsp;

internal sealed partial class RoslynConnection
{
    internal const int MaximumOpenDocuments = 1000;
    private const int MaximumDocumentCharacters = 1024 * 1024;
    private const int DocumentReadBufferCharacters = 4096;
    private const int InitialDocumentVersion = 1;
    private static readonly ToolError _oversizedDocument = ToolError.InvalidRequest(
        "The file exceeds the one million UTF-16 code unit limit."
    );

    // Held for document state and its notifications, never across a Roslyn request or wait
    private readonly SemaphoreSlim _documentLock = new(1, 1);

    private readonly Dictionary<string, OpenDocument> _documents = [with(StringComparer.Ordinal)];
    private readonly LinkedList<string> _openedOrder = [];
    private readonly ConcurrentDictionary<string, byte> _pendingFiles = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _createdFiles = new(StringComparer.Ordinal);
    private Dictionary<string, FileStamp> _workspaceFiles = [with(StringComparer.Ordinal)];

    private FileSystemWatcher? _watcher;
    private int _scanRequested;

    public async Task<Dictionary<string, int>> CopyDocumentVersionsAsync(
        CancellationToken cancellationToken
    )
    {
        using var held = await LockDocumentsAsync(cancellationToken);
        return _documents.ToDictionary(
            document => document.Key,
            document => document.Value.Version,
            StringComparer.Ordinal
        );
    }

    /// <returns>The document URI.</returns>
    public async Task<Result<string>> OpenDocumentAsync(
        string file,
        CancellationToken cancellationToken
    )
    {
        using var held = await LockDocumentsAsync(cancellationToken);
        return await OpenDocumentCoreAsync(file, cancellationToken);
    }

    /// <summary>
    /// Opens the documents one at a time, so their versions stay in order.
    /// </summary>
    public async Task<Result<List<string>>> OpenDocumentsAsync(
        IReadOnlyCollection<string> files,
        CancellationToken cancellationToken
    )
    {
        using var held = await LockDocumentsAsync(cancellationToken);
        var uris = new List<string>(files.Count);
        foreach (var file in files)
        {
            if (
                (await OpenDocumentCoreAsync(file, cancellationToken)).IsError(
                    out var error,
                    out var uri
                )
            )
            {
                return error;
            }

            uris.Add(uri);
        }

        return uris;
    }

    public Task<Result> SynchronizeAsync(CancellationToken cancellationToken) =>
        BridgeTelemetry.TraceAsync(
            "roslyn_bridge.workspace.sync",
            BridgeTelemetry.SyncDuration,
            () => SynchronizeAllAsync(cancellationToken)
        );

    /// <summary>
    /// Sends files the bridge changed without waiting for the file watcher.
    /// </summary>
    public Task<Result> SynchronizeFilesAsync(
        IEnumerable<string> files,
        CancellationToken cancellationToken
    )
    {
        foreach (var file in files)
        {
            QueueFile(file);
        }

        return SynchronizeAsync(cancellationToken);
    }

    /// <summary>
    /// Announces requested files that the file watcher has not reported yet.
    /// Without the creation notice, Roslyn keeps a new file outside its project.
    /// </summary>
    public async Task<Result> SynchronizeNewFilesAsync(
        IEnumerable<string> files,
        CancellationToken cancellationToken
    )
    {
        List<string> unknown;
        using (await LockDocumentsAsync(cancellationToken))
        {
            unknown = [.. files.Where(file => !_workspaceFiles.ContainsKey(file))];
        }

        return await SynchronizeFilesAsync(unknown, cancellationToken);
    }

    /// <summary>
    /// Sends every difference from what Roslyn last received. Returns the stamps Roslyn now has.
    /// </summary>
    public async Task<Result<Dictionary<string, FileStamp>>> SynchronizeWorkspaceAsync(
        CancellationToken cancellationToken
    )
    {
        Interlocked.Exchange(ref _scanRequested, 1);
        if (await SynchronizeAsync(cancellationToken) is ToolError error)
            return error;

        using var held = await LockDocumentsAsync(cancellationToken);
        return new Dictionary<string, FileStamp>(_workspaceFiles, StringComparer.Ordinal);
    }

    public async Task<Result<string[]>> WorkspaceFilesAsync(CancellationToken cancellationToken)
    {
        if (await SynchronizeAsync(cancellationToken) is ToolError error)
            return error;

        using var held = await LockDocumentsAsync(cancellationToken);
        return _workspaceFiles.Keys.ToArray();
    }

    private async Task<Result<string>> OpenDocumentCoreAsync(
        string file,
        CancellationToken cancellationToken
    )
    {
        if (paths.Resolve(file).IsError(out var pathError, out var path))
            return pathError;

        if (!WorkspacePaths.IsCSharpFile(path))
        {
            return ToolError.InvalidRequest(
                $"Only C# files can be opened: {paths.RelativePath(path)}"
            );
        }

        if (FileStamp.Read(path) is not { } stamp)
            return ToolError.InvalidRequest($"The file does not exist: {paths.RelativePath(path)}");

        var uri = new Uri(path).AbsoluteUri;
        if (_documents.TryGetValue(path, out var open))
        {
            // Least recently used documents close first, so callers can keep a batch open
            _openedOrder.Remove(open.Node);
            _openedOrder.AddLast(open.Node);
            return uri;
        }

        if (_documents.Count >= MaximumOpenDocuments)
        {
            var closed = await CloseDocumentAsync(_openedOrder.First!.Value, cancellationToken);
            if (closed is ToolError closeError)
                return closeError;
        }

        if (
            (await ReadDocumentAsync(path, cancellationToken)).IsError(
                out var readError,
                out var text
            )
        )
        {
            return readError;
        }

        var opened = await NotifyAsync(
            LspMethods.TextDocumentDidOpen,
            new JsonObject
            {
                ["textDocument"] = new JsonObject
                {
                    ["uri"] = uri,
                    ["languageId"] = "csharp",
                    ["version"] = InitialDocumentVersion,
                    ["text"] = text,
                },
            },
            cancellationToken
        );
        if (opened is ToolError openError)
            return openError;

        _documents.Add(
            path,
            new OpenDocument(text, InitialDocumentVersion, _openedOrder.AddLast(path), stamp)
        );

        return uri;
    }

    private async Task<Result> SynchronizeAllAsync(CancellationToken cancellationToken)
    {
        using var held = await LockDocumentsAsync(cancellationToken);
        foreach (var path in _documents.Keys.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await SynchronizeDocumentAsync(path, cancellationToken) is ToolError error)
                return error;
        }

        return await SynchronizeWatchedFilesAsync(cancellationToken);
    }

    private static async Task<Result<string>> ReadDocumentAsync(
        string path,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await using var stream = new FileStream(
                path,
                new FileStreamOptions
                {
                    Access = FileAccess.Read,
                    Mode = FileMode.Open,
                    Share = FileShare.ReadWrite | FileShare.Delete,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                }
            );
            using var reader = new StreamReader(
                stream,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true
            );
            var builder = new StringBuilder();
            var buffer = new char[DocumentReadBufferCharacters];
            while (true)
            {
                var count = await reader.ReadAsync(buffer, cancellationToken);
                if (count == 0)
                    return builder.ToString();

                if (builder.Length + count > MaximumDocumentCharacters)
                    return _oversizedDocument;

                builder.Append(buffer, 0, count);
            }
        }
        catch (Exception exception) when (ToolError.IsIo(exception))
        {
            return ToolError.Io(exception.Message);
        }
    }

    private async Task<Result> SynchronizeDocumentAsync(
        string path,
        CancellationToken cancellationToken
    )
    {
        if (FileStamp.Read(path) is not { } stamp)
            return await CloseDocumentAsync(path, cancellationToken);

        var document = _documents[path];
        if (stamp == document.Stamp)
            return Result.Success;

        if (
            (await ReadDocumentAsync(path, cancellationToken)).IsError(
                out var readError,
                out var text
            )
        )
        {
            if (!IsOversized(readError))
                return readError;

            // Roslyn reads the file from disk once the document closes
            LogOversizedDocument(logger, path);
            return await CloseDocumentAsync(path, cancellationToken);
        }

        if (string.Equals(document.Text, text, StringComparison.Ordinal))
        {
            _documents[path] = document with { Stamp = stamp };
            return Result.Success;
        }

        var version = document.Version + 1;
        var changed = await NotifyAsync(
            LspMethods.TextDocumentDidChange,
            new JsonObject
            {
                ["textDocument"] = new JsonObject
                {
                    ["uri"] = new Uri(path).AbsoluteUri,
                    ["version"] = version,
                },
                // A change without a range replaces the whole document
                ["contentChanges"] = new JsonArray(new JsonObject { ["text"] = text }),
            },
            cancellationToken
        );
        if (changed is not ToolError)
            _documents[path] = document with { Text = text, Version = version, Stamp = stamp };

        return changed;
    }

    // The path comes from the workspace walk or the watcher. Opening it runs the symlink check
    private async Task<Result> SynchronizeClosedDocumentAsync(
        string path,
        CancellationToken cancellationToken
    )
    {
        if (_documents.ContainsKey(path) || !WorkspacePaths.IsCSharpFile(path))
            return Result.Success;

        // Opening the document sends its saved text in order, unlike asynchronous watched-file notifications
        if ((await OpenDocumentCoreAsync(path, cancellationToken)).IsError(out var error, out _))
        {
            if (!IsOversized(error))
                return error;

            LogOversizedDocument(logger, path);
            return Result.Success;
        }

        return await CloseDocumentAsync(path, cancellationToken);
    }

    private async Task<Result> CloseDocumentAsync(string path, CancellationToken cancellationToken)
    {
        var closed = await NotifyAsync(
            LspMethods.TextDocumentDidClose,
            new JsonObject
            {
                ["textDocument"] = new JsonObject { ["uri"] = new Uri(path).AbsoluteUri },
            },
            cancellationToken
        );
        if (closed is not ToolError && _documents.Remove(path, out var document))
            _openedOrder.Remove(document.Node);

        return closed;
    }

    private static bool IsOversized(ToolError error) => ReferenceEquals(error, _oversizedDocument);

    private Result<Dictionary<string, FileStamp>> ScanWorkspace(
        CancellationToken cancellationToken
    ) =>
        paths
            .EnumerateWatchedFiles(cancellationToken)
            .Map(found =>
                found.ToDictionary(file => file.Path, file => file.Stamp, StringComparer.Ordinal)
            );

    private Task<Result> NotifyFileChangesAsync(
        Dictionary<string, FileStamp> current,
        HashSet<string> changedPaths,
        CancellationToken cancellationToken
    )
    {
        var changes = new List<FileEvent>();
        foreach (var (path, stamp) in current)
        {
            if (!_workspaceFiles.TryGetValue(path, out var previous))
            {
                changes.Add(new FileEvent(path, FileEventType.Created));
            }
            else if (changedPaths.Contains(path) || stamp != previous)
            {
                changes.Add(new FileEvent(path, FileEventType.Changed));
            }
        }

        foreach (var path in _workspaceFiles.Keys.Where(path => !current.ContainsKey(path)))
        {
            changes.Add(new FileEvent(path, FileEventType.Deleted));
        }

        return SendFileChangesAsync(changes, cancellationToken);
    }

    private async ValueTask<LockRelease> LockDocumentsAsync(CancellationToken cancellationToken)
    {
        await _documentLock.WaitAsync(cancellationToken);
        return new LockRelease(_documentLock);
    }

    private sealed record OpenDocument(
        string Text,
        int Version,
        LinkedListNode<string> Node,
        FileStamp Stamp
    );

    private readonly struct LockRelease(SemaphoreSlim semaphore) : IDisposable
    {
        public void Dispose() => semaphore.Release();
    }
}
