// Copyright (C) RoslynBridge contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Frozen;
using System.Security.Cryptography;
using RoslynBridge.Tools;

namespace RoslynBridge.Editing;

internal sealed partial class WorkspaceEditService
{
    internal const int MaximumSnapshotBytes = 256 * BytesPerMebibyte;

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

    public async Task<WorkspaceSnapshot> CaptureAsync(CancellationToken cancellationToken)
    {
        var fingerprints = await CaptureFingerprintsAsync(cancellationToken);
        return new WorkspaceSnapshot(fingerprints, FrozenDictionary<string, int>.Empty);
    }

    private async Task<Dictionary<string, string>> CaptureFingerprintsAsync(
        CancellationToken cancellationToken
    )
    {
        var fingerprints = new Dictionary<string, string>(StringComparer.Ordinal);
        var totalBytes = 0L;
        foreach (var (file, length, _) in paths.EnumerateFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsSourceFile(file))
            {
                continue;
            }

            totalBytes += length;
            if (totalBytes > MaximumSnapshotBytes)
            {
                throw new ToolErrorException(
                    ToolError.InvalidRequest(
                        $"The workspace snapshot exceeds {MaximumSnapshotBytes / BytesPerMebibyte} MiB of source and project files."
                    )
                );
            }

            fingerprints.Add(file, await FingerprintAsync(file, cancellationToken));
        }

        return fingerprints;
    }

    /// <summary>
    /// Throws a <see cref="ToolErrorException"/> with a stale edit error when any workspace file changed since the snapshot.
    /// </summary>
    public async Task EnsureCurrentAsync(
        WorkspaceSnapshot snapshot,
        CancellationToken cancellationToken
    )
    {
        var current = await CaptureFingerprintsAsync(cancellationToken);
        if (
            current.Count != snapshot.Fingerprints.Count
            || snapshot.Fingerprints.Any(pair =>
                !current.TryGetValue(pair.Key, out var hash)
                || !StringComparer.Ordinal.Equals(pair.Value, hash)
            )
        )
        {
            throw new ToolErrorException(
                ToolError.StaleEdit(
                    "Workspace files changed after Roslyn computed this edit. Request the action again."
                )
            );
        }
    }

    private static async Task<byte[]?> ReadOriginalAsync(
        string path,
        WorkspaceSnapshot snapshot,
        CancellationToken cancellationToken
    )
    {
        if (!File.Exists(path))
        {
            if (snapshot.Fingerprints.ContainsKey(path))
            {
                throw new ToolErrorException(
                    ToolError.StaleEdit($"The source file was deleted: {path}")
                );
            }

            return null;
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: UnbufferedStreamBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan
        );
        if (stream.Length > MaximumFileBytes)
        {
            throw new ToolErrorException(
                ToolError.InvalidRequest(
                    $"The source file exceeds {MaximumFileBytes / BytesPerMebibyte} MiB: {path}"
                )
            );
        }

        var content = new byte[(int)stream.Length];
        await stream.ReadExactlyAsync(content, cancellationToken);
        var fingerprint = Convert.ToHexString(SHA256.HashData(content));
        if (
            stream.Length != content.Length
            || !snapshot.Fingerprints.TryGetValue(path, out var expected)
            || !StringComparer.Ordinal.Equals(expected, fingerprint)
        )
        {
            throw new ToolErrorException(
                ToolError.StaleEdit($"The source file changed after the request: {path}")
            );
        }

        return content;
    }

    private string ResolveEditPath(string value)
    {
        if (
            !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !uri.IsFile
            || !string.IsNullOrEmpty(uri.Host)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
        )
        {
            throw new ToolErrorException(
                ToolError.InvalidLspData("Workspace edits require local file URIs.")
            );
        }

        var resolved = paths.Resolve(WorkspacePaths.LocalPath(uri));
        if (!IsSourceFile(resolved))
        {
            throw new ToolErrorException(
                ToolError.InvalidRequest(
                    "Workspace edits require a supported source or project file extension."
                )
            );
        }
        if (paths.IsExcluded(resolved))
        {
            throw new ToolErrorException(
                ToolError.InvalidRequest(
                    "Workspace edits cannot target excluded workspace directories."
                )
            );
        }

        return resolved;
    }

    private static bool IsSourceFile(string path) =>
        _sourceExtensions.Contains(Path.GetExtension(path));

    private static async Task<string> FingerprintAsync(
        string path,
        CancellationToken cancellationToken
    )
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: UnbufferedStreamBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan
        );
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }
}
