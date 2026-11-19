// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Security.Cryptography;

namespace RoslynBridge.Editing;

internal sealed partial class WorkspaceEditService
{
    private const int UnbufferedStreamBufferSize = 1;

    private static readonly FrozenSet<string> _sourceExtensions = new[]
    {
        ".cs",
        ".csx",
        ".csproj",
        ".props",
        ".targets",
        ".sln",
        ".slnx",
        ".json",
        ".config",
        ".editorconfig",
        ".globalconfig",
        ".razor",
        ".cshtml",
        ".resx",
        ".xaml",
        ".txt",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    // Changes to these files can change what an edit means, even when the edit does not touch them
    private static readonly FrozenSet<string> _projectExtensions = new[]
    {
        ".csproj",
        ".props",
        ".targets",
        ".sln",
        ".slnx",
        ".editorconfig",
        ".globalconfig",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly ToolError _staleWorkspace = ToolError.StaleEdit(
        "Workspace files changed after Roslyn computed this edit. Request the action again."
    );

    private static Dictionary<string, string> Fingerprints(List<EditedDocument> documents) =>
        documents
            .Where(document => document.Original is not null)
            .ToDictionary(
                document => document.OriginalPath,
                document => Convert.ToHexString(SHA256.HashData(document.Original!)),
                StringComparer.Ordinal
            );

    /// <returns>A stale edit error when a touched file or a project file changed since the snapshot.</returns>
    private Result EnsureCurrent(
        List<EditedDocument> documents,
        WorkspaceSnapshot snapshot,
        CancellationToken cancellationToken
    )
    {
        // A case-only rename targets the same file under another spelling
        var touched = documents.SelectMany(document =>
            document.CaseOnlyRename
                ? [document.OriginalPath]
                : (string[])[document.OriginalPath, document.Path]
        );
        foreach (var path in touched.Distinct(StringComparer.Ordinal))
        {
            if (FileStamp.Read(path) != Expected(snapshot, path))
            {
                return _staleWorkspace;
            }
        }

        if (paths.EnumerateFiles(cancellationToken).IsError(out var error, out var files))
        {
            return error;
        }

        // Build output under obj also holds generated props and editorconfig files, so only sources count
        var projectFiles = 0;
        foreach (var file in files.Where(file => IsProjectFile(file.Path)))
        {
            projectFiles++;
            if (!snapshot.Files.TryGetValue(file.Path, out var stamp) || stamp != file.Stamp)
            {
                return _staleWorkspace;
            }
        }

        var snapshotProjectFiles = snapshot
            .Files.Keys.Where(path => IsProjectFile(path) && !paths.IsExcluded(path))
            .Take(projectFiles + 1)
            .Count();
        return projectFiles == snapshotProjectFiles ? Result.Success : _staleWorkspace;
    }

    private static FileStamp? Expected(WorkspaceSnapshot snapshot, string path) =>
        snapshot.Files.TryGetValue(path, out var stamp) ? stamp : null;

    /// <returns>Null content for a file that does not exist.</returns>
    private static async Task<Result<byte[]?>> ReadOriginalAsync(
        string path,
        WorkspaceSnapshot snapshot,
        CancellationToken cancellationToken
    )
    {
        var expected = Expected(snapshot, path);
        if (FileStamp.Read(path) is not { } stamp)
        {
            if (expected is null)
            {
                return (byte[]?)null;
            }

            return ToolError.StaleEdit($"The source file was deleted: {path}");
        }

        if (stamp != expected)
        {
            return ChangedFile(path);
        }

        if (stamp.Length > MaximumFileBytes)
        {
            return ToolError.InvalidRequest(
                $"The source file exceeds {MaximumFileBytes / BytesPerMebibyte} MiB: {path}"
            );
        }

        var content = new byte[stamp.Length];
        long length;
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: UnbufferedStreamBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan
            );
            await stream.ReadExactlyAsync(content, cancellationToken);
            length = stream.Length;
        }
        catch (Exception exception) when (ToolError.IsIo(exception))
        {
            return ToolError.Io(exception.Message);
        }

        if (
            length != content.Length
            || (
                snapshot.Fingerprints.TryGetValue(path, out var fingerprint)
                && !StringComparer.Ordinal.Equals(
                    fingerprint,
                    Convert.ToHexString(SHA256.HashData(content))
                )
            )
        )
        {
            return ChangedFile(path);
        }

        return content;
    }

    private static ToolError ChangedFile(string path) =>
        ToolError.StaleEdit($"The source file changed after the request: {path}");

    private Result<string> ResolveEditPath(string value)
    {
        if (
            !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !uri.IsFile
            || !string.IsNullOrEmpty(uri.Host)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
        )
        {
            return ToolError.InvalidLspData("Workspace edits require local file URIs.");
        }

        if (paths.Resolve(WorkspacePaths.LocalPath(uri)).IsError(out var error, out var resolved))
        {
            return error;
        }

        if (!IsSourceFile(resolved))
        {
            return ToolError.InvalidRequest(
                "Workspace edits require a supported source or project file extension."
            );
        }

        if (paths.IsExcluded(resolved))
        {
            return ToolError.InvalidRequest(
                "Workspace edits cannot target excluded workspace directories."
            );
        }

        return resolved;
    }

    private static bool IsSourceFile(string path) =>
        _sourceExtensions.Contains(Path.GetExtension(path));

    private static bool IsProjectFile(string path) =>
        _projectExtensions.Contains(Path.GetExtension(path));
}
