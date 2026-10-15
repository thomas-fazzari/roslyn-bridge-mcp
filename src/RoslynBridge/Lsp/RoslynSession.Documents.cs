// Copyright (C) RoslynBridge contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;

namespace RoslynBridge.Lsp;

internal sealed partial class RoslynSession
{
    internal const int MaximumOpenDocuments = 128;
    private const int MaximumDocumentCharacters = 1024 * 1024;
    private const int DocumentReadBufferCharacters = 4096;
    private const int InitialDocumentVersion = 1;
    private const int FileCreated = 1;
    private const int FileChanged = 2;
    private const int FileDeleted = 3;

    private readonly Dictionary<string, OpenDocument> _documents = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _openedOrder = [];
    private readonly ConcurrentDictionary<string, byte> _watchRegistrations = new(
        StringComparer.Ordinal
    );
    private readonly ConcurrentDictionary<string, byte> _pendingFiles = new(StringComparer.Ordinal);
    private Dictionary<string, FileStamp> _workspaceFiles = new(StringComparer.Ordinal);
    private readonly HashSet<string> _createdFiles = new(StringComparer.Ordinal);

    private FileSystemWatcher? _watcher;
    private int _scanRequested;

    public Dictionary<string, int> CopyDocumentVersions() =>
        _documents.ToDictionary(
            document => document.Key,
            document => document.Value.Version,
            StringComparer.Ordinal
        );

    public async Task<string> OpenDocumentAsync(string file, CancellationToken cancellationToken)
    {
        var path = paths.Resolve(file);
        if (!IsCSharpFile(path))
        {
            throw new ToolErrorException(
                ToolError.InvalidRequest($"Only C# files can be opened: {paths.RelativePath(path)}")
            );
        }

        if (!File.Exists(path))
        {
            throw new ToolErrorException(
                ToolError.InvalidRequest($"The file does not exist: {paths.RelativePath(path)}")
            );
        }

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
            var oldest =
                _openedOrder.First
                ?? throw new InvalidOperationException("The open document order is empty.");
            await CloseDocumentAsync(oldest.Value, cancellationToken);
        }

        var text = await ReadDocumentAsync(path, cancellationToken);
        await NotifyAsync(
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

        _documents.Add(
            path,
            new OpenDocument(text, InitialDocumentVersion, _openedOrder.AddLast(path))
        );

        return uri;
    }

    /// <summary>
    /// Opens the documents one at a time, so their versions stay in order, and returns their URIs.
    /// </summary>
    public async Task<List<string>> OpenDocumentsAsync(
        IReadOnlyCollection<string> files,
        CancellationToken cancellationToken
    )
    {
        var uris = new List<string>(files.Count);
        foreach (var file in files)
        {
            uris.Add(await OpenDocumentAsync(file, cancellationToken));
        }

        return uris;
    }

    public async Task SynchronizeAsync(CancellationToken cancellationToken)
    {
        foreach (var path in _documents.Keys.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await SynchronizeDocumentAsync(path, cancellationToken);
        }

        await SynchronizeWatchedFilesAsync(cancellationToken);
    }

    /// <summary>
    /// Sends files the bridge wrote, created or deleted, without waiting for the file watcher.
    /// </summary>
    public async Task SynchronizeFilesAsync(
        IEnumerable<string> files,
        CancellationToken cancellationToken
    )
    {
        foreach (var file in files)
        {
            QueueFile(file);
        }

        await SynchronizeAsync(cancellationToken);
    }

    /// <summary>
    /// Announces requested files that the file watcher has not reported yet.
    /// Without the creation notice, Roslyn keeps a new file outside its project.
    /// </summary>
    public Task SynchronizeNewFilesAsync(
        IEnumerable<string> files,
        CancellationToken cancellationToken
    ) =>
        SynchronizeFilesAsync(
            files.Where(file => !_workspaceFiles.ContainsKey(file)),
            cancellationToken
        );

    /// <summary>
    /// Compares every watched file with what Roslyn last received and sends the differences,
    /// without waiting for the file watcher. Returns the file stamps Roslyn now has.
    /// </summary>
    public async Task<Dictionary<string, FileStamp>> SynchronizeWorkspaceAsync(
        CancellationToken cancellationToken
    )
    {
        Interlocked.Exchange(ref _scanRequested, 1);
        await SynchronizeAsync(cancellationToken);
        return new Dictionary<string, FileStamp>(_workspaceFiles, StringComparer.Ordinal);
    }

    internal void RegisterCapabilities(JsonObject parameters)
    {
        if (parameters["registrations"] is not JsonArray registrations)
        {
            return;
        }

        foreach (var registration in registrations.OfType<JsonObject>())
        {
            if (
                string.Equals(
                    registration["method"]?.GetValue<string>(),
                    LspMethods.WorkspaceDidChangeWatchedFiles,
                    StringComparison.Ordinal
                ) && registration["id"]?.GetValue<string>() is { } id
            )
            {
                _watchRegistrations[id] = 0;
            }
        }
    }

    internal void UnregisterCapabilities(JsonObject parameters)
    {
        // The misspelling is part of the LSP 3.17 protocol
        if (parameters["unregisterations"] is not JsonArray registrations)
        {
            return;
        }

        foreach (var registration in registrations.OfType<JsonObject>())
        {
            if (registration["id"]?.GetValue<string>() is { } id)
            {
                _watchRegistrations.TryRemove(id, out _);
            }
        }
    }

    private static async Task<string> ReadDocumentAsync(
        string path,
        CancellationToken cancellationToken
    )
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
            {
                return builder.ToString();
            }

            if (builder.Length + count > MaximumDocumentCharacters)
            {
                throw new InvalidDataException(
                    "The document exceeded the one million UTF-16 code unit limit."
                );
            }

            builder.Append(buffer, 0, count);
        }
    }

    private async Task SynchronizeDocumentAsync(string path, CancellationToken cancellationToken)
    {
        var uri = paths.ToUri(path);
        if (!File.Exists(path))
        {
            await CloseDocumentAsync(path, cancellationToken);
            return;
        }

        string text;
        try
        {
            text = await ReadDocumentAsync(path, cancellationToken);
        }
        catch (InvalidDataException)
        {
            // Roslyn reads the file from disk once the document closes
            LogOversizedDocument(logger, path);
            await CloseDocumentAsync(path, cancellationToken);
            return;
        }

        var document = _documents[path];
        if (string.Equals(document.Text, text, StringComparison.Ordinal))
        {
            return;
        }

        var version = document.Version + 1;
        await NotifyAsync(
            LspMethods.TextDocumentDidChange,
            new JsonObject
            {
                ["textDocument"] = new JsonObject { ["uri"] = uri, ["version"] = version },
                // A change without a range replaces the whole document
                ["contentChanges"] = new JsonArray(new JsonObject { ["text"] = text }),
            },
            cancellationToken
        );
        _documents[path] = document with { Text = text, Version = version };
    }

    // The path comes from the workspace walk or the watcher. Opening it runs the symlink check
    private async Task SynchronizeClosedDocumentAsync(
        string path,
        CancellationToken cancellationToken
    )
    {
        if (_documents.ContainsKey(path) || !IsCSharpFile(path))
        {
            return;
        }

        // Opening the document sends its saved text in order, unlike asynchronous watched-file notifications
        try
        {
            await OpenDocumentAsync(path, cancellationToken);
        }
        catch (InvalidDataException)
        {
            LogOversizedDocument(logger, path);
            return;
        }

        await CloseDocumentAsync(path, cancellationToken);
    }

    internal static bool IsCSharpFile(string path) =>
        path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".csx", StringComparison.OrdinalIgnoreCase);

    private async Task CloseDocumentAsync(string path, CancellationToken cancellationToken)
    {
        await NotifyAsync(
            LspMethods.TextDocumentDidClose,
            new JsonObject
            {
                ["textDocument"] = new JsonObject { ["uri"] = new Uri(path).AbsoluteUri },
            },
            cancellationToken
        );
        if (_documents.Remove(path, out var document))
        {
            _openedOrder.Remove(document.Node);
        }
    }

    private Dictionary<string, FileStamp> ScanWorkspace(CancellationToken cancellationToken)
    {
        var files = new Dictionary<string, FileStamp>(StringComparer.Ordinal);
        foreach (var file in paths.EnumerateWatchedFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            files.Add(file.Path, file.Stamp);
        }

        return files;
    }

    private async Task NotifyFileChangesAsync(
        Dictionary<string, FileStamp> current,
        HashSet<string> changedPaths,
        CancellationToken cancellationToken
    )
    {
        var changes = new List<FileChange>();
        foreach (var (path, stamp) in current)
        {
            if (!_workspaceFiles.TryGetValue(path, out var previous))
            {
                changes.Add(new FileChange(path, FileCreated));
            }
            else if (changedPaths.Contains(path) || stamp != previous)
            {
                changes.Add(new FileChange(path, FileChanged));
            }
        }

        foreach (var path in _workspaceFiles.Keys.Where(path => !current.ContainsKey(path)))
        {
            changes.Add(new FileChange(path, FileDeleted));
        }

        await SendFileChangesAsync(changes, cancellationToken);
    }

    private sealed record OpenDocument(string Text, int Version, LinkedListNode<string> Node);
}
