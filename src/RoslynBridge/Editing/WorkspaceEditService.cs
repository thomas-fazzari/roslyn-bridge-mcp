// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Results;
using RoslynBridge.Telemetry;

namespace RoslynBridge.Editing;

internal sealed partial class WorkspaceEditService(WorkspacePaths paths)
{
    internal const int MaximumFileBytes = 8 * BytesPerMebibyte;

    private const int BytesPerMebibyte = 1024 * 1024;
    private const int MaximumEditBytes = 64 * BytesPerMebibyte;
    private const int MaximumEditedFiles = 256;

    /// <summary>
    /// The returned snapshot holds the fingerprints of the files the edit reads.
    /// </summary>
    public async Task<Result<EditPreview>> PreviewAsync(
        WorkspaceEdit edit,
        WorkspaceSnapshot snapshot,
        CancellationToken cancellationToken
    )
    {
        var planned = await PlanAsync(edit, snapshot, cancellationToken);
        if (planned.IsError(out var planError, out var documents))
            return planError;

        return EnsureCurrent(documents, snapshot, cancellationToken) is ToolError staleError
            ? staleError
            : documents
                .Where(document => document.Changed)
                .Select(document => document.Describe(paths))
                .All()
                .Map(files => new EditPreview(
                    files,
                    snapshot with
                    {
                        Fingerprints = Fingerprints(documents),
                    }
                ));
    }

    /// <summary>
    /// Returns an <see cref="EditFailure"/> when the edit fails after it changed a file.
    /// </summary>
    public async Task<Result<EditReceipt>> ApplyAsync(
        WorkspaceEdit edit,
        WorkspaceSnapshot snapshot,
        CancellationToken cancellationToken,
        ActivityContext parent = default
    )
    {
        using var activity = BridgeTelemetry.Source.StartActivity(
            "roslyn_bridge.edit.apply",
            ActivityKind.Internal,
            parent
        );
        try
        {
            var result = await ApplyPlannedAsync(edit, snapshot, cancellationToken);
            if (result is ToolError error)
                BridgeTelemetry.MarkFailed(activity, error.Code);

            return result;
        }
        catch (Exception exception)
        {
            BridgeTelemetry.MarkFailed(activity, BridgeTelemetry.ErrorTypeOf(exception));
            throw;
        }
    }

    private async Task<Result<EditReceipt>> ApplyPlannedAsync(
        WorkspaceEdit edit,
        WorkspaceSnapshot snapshot,
        CancellationToken cancellationToken
    )
    {
        var planned = await PlanAsync(edit, snapshot, cancellationToken);
        if (planned.IsError(out var planError, out var documents))
            return planError;

        var temporaryFiles = new Dictionary<string, string>(StringComparer.Ordinal);
        var completedOperations = new List<FileOperation>();
        ApplyFailure? failure;
        (ToolError Error, string Path)? cleanupFailure;
        try
        {
            failure = await ApplyMutationsAsync(
                documents,
                snapshot,
                temporaryFiles,
                completedOperations,
                cancellationToken
            );
        }
        finally
        {
            cleanupFailure = CleanupTemporaryFiles(temporaryFiles);
        }

        BridgeTelemetry.EditOperations.Record(completedOperations.Count);
        return CompleteApply(completedOperations, failure, cleanupFailure);
    }

    private static Result<EditReceipt> CompleteApply(
        List<FileOperation> completedOperations,
        ApplyFailure? failure,
        (ToolError Error, string Path)? cleanupFailure
    )
    {
        if (failure is not null)
        {
            return completedOperations.Count == 0
                ? failure.Error
                : new EditFailure(
                    failure.Error,
                    new EditReceipt(Applied: false, completedOperations),
                    EditApplicationPhase.Write,
                    failure.Mutation?.Path,
                    failure.Mutation?.Operation
                );
        }

        if (cleanupFailure is { Error: var cleanupError, Path: var cleanupPath })
        {
            return new EditFailure(
                cleanupError,
                new EditReceipt(Applied: true, completedOperations),
                EditApplicationPhase.Cleanup,
                cleanupPath
            );
        }

        return new EditReceipt(Applied: true, completedOperations);
    }

    private async Task<ApplyFailure?> ApplyMutationsAsync(
        List<EditedDocument> documents,
        WorkspaceSnapshot snapshot,
        Dictionary<string, string> temporaryFiles,
        List<FileOperation> completedOperations,
        CancellationToken cancellationToken
    )
    {
        PendingMutation? pendingMutation = null;
        try
        {
            if (await StageAsync(documents, temporaryFiles, cancellationToken) is ToolError error)
                return new ApplyFailure(error, Mutation: null);

            if (EnsureCurrent(documents, snapshot, cancellationToken) is ToolError staleError)
                return new ApplyFailure(staleError, Mutation: null);

            cancellationToken.ThrowIfCancellationRequested();
            var written = ApplyDestinationWrites(
                documents,
                temporaryFiles,
                completedOperations,
                ref pendingMutation
            );
            if (written is ToolError writeError)
                return new ApplyFailure(writeError, pendingMutation);

            ApplyCaseOnlyRenames(documents, completedOperations, ref pendingMutation);
            var deleted = ApplySourceDeletions(documents, completedOperations, ref pendingMutation);
            return deleted is ToolError deleteError
                ? new ApplyFailure(deleteError, pendingMutation)
                : null;
        }
        catch (Exception exception) when (ToolError.IsIo(exception))
        {
            return new ApplyFailure(ToolError.Io(exception.Message), pendingMutation);
        }
    }

    private Result ApplyDestinationWrites(
        List<EditedDocument> documents,
        Dictionary<string, string> temporaryFiles,
        List<FileOperation> completedOperations,
        ref PendingMutation? pendingMutation
    )
    {
        foreach (
            var document in documents.Where(document =>
                document is { Changed: true, Content: not null }
            )
        )
        {
            var destination = document.Path;
            var temporary = temporaryFiles[destination];
            pendingMutation = new(paths.RelativePath(destination), DestinationOperation(document));
            if (paths.Resolve(destination) is ToolError error)
                return error;

            File.Move(temporary, destination, overwrite: true);
            completedOperations.Add(
                new(
                    paths.RelativePath(
                        document.CaseOnlyRename ? document.OriginalPath : destination
                    ),
                    DestinationOperation(document)
                )
            );
        }

        return Result.Success;
    }

    private void ApplyCaseOnlyRenames(
        List<EditedDocument> documents,
        List<FileOperation> completedOperations,
        ref PendingMutation? pendingMutation
    )
    {
        foreach (
            var document in documents.Where(document =>
                document is { CaseOnlyRename: true, Content: not null }
            )
        )
        {
            pendingMutation = new(paths.RelativePath(document.Path), FileOperation.Rename);
            File.Move(document.OriginalPath, document.Path, overwrite: true);
            completedOperations.Add(
                new(
                    paths.RelativePath(document.Path),
                    FileOperation.Rename,
                    paths.RelativePath(document.OriginalPath)
                )
            );
        }
    }

    private Result ApplySourceDeletions(
        List<EditedDocument> documents,
        List<FileOperation> completedOperations,
        ref PendingMutation? pendingMutation
    )
    {
        foreach (
            var document in documents.Where(document =>
                document is { Changed: true, Content: null }
            )
        )
        {
            pendingMutation = new(paths.RelativePath(document.Path), FileOperation.Delete);
            if (paths.Resolve(document.Path) is ToolError error)
                return error;

            File.Delete(document.Path);
            completedOperations.Add(new(paths.RelativePath(document.Path), FileOperation.Delete));
        }

        return Result.Success;
    }

    private (ToolError Error, string Path)? CleanupTemporaryFiles(
        Dictionary<string, string> temporaryFiles
    )
    {
        (ToolError Error, string Path)? failure = null;
        foreach (var temporary in temporaryFiles.Values)
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception exception) when (ToolError.IsIo(exception))
            {
                failure ??= (ToolError.Io(exception.Message), paths.RelativePath(temporary));
            }
        }

        return failure;
    }

    private async Task<Result> StageAsync(
        List<EditedDocument> documents,
        Dictionary<string, string> temporaryFiles,
        CancellationToken cancellationToken
    )
    {
        foreach (
            var document in documents.Where(document =>
                document is { Changed: true, Content: not null }
            )
        )
        {
            if (paths.Resolve(document.Path) is ToolError error)
                return error;

            Directory.CreateDirectory(Path.GetDirectoryName(document.Path)!);
            var temporary = $"{document.Path}.{Guid.NewGuid():N}.tmp";
            temporaryFiles.Add(document.Path, temporary);
            await File.WriteAllBytesAsync(temporary, document.Content!, cancellationToken);
            if (!OperatingSystem.IsWindows() && document.ModeSource is { } modeSource)
                File.SetUnixFileMode(temporary, File.GetUnixFileMode(modeSource));
        }

        return Result.Success;
    }

    private async Task<Result<List<EditedDocument>>> PlanAsync(
        WorkspaceEdit edit,
        WorkspaceSnapshot snapshot,
        CancellationToken cancellationToken
    )
    {
        var documents = new Dictionary<string, EditedDocument>(StringComparer.Ordinal);
        foreach (var operation in edit.Operations)
        {
            if (
                await PlanOperationAsync(operation, documents, snapshot, cancellationToken)
                is ToolError operationError
            )
            {
                return operationError;
            }

            if (CheckSize(documents.Values) is ToolError sizeError)
                return sizeError;
        }

        if (CheckSize(documents.Values) is ToolError error)
            return error;

        return ValidateParents(documents) is ToolError parentsError
            ? parentsError
            : documents.Values.ToList();
    }

    private Result ValidateParents(Dictionary<string, EditedDocument> documents)
    {
        foreach (var document in documents.Values.Where(document => document.Content is not null))
        {
            var parent = Path.GetDirectoryName(document.Path);
            while (parent is not null && !StringComparer.Ordinal.Equals(parent, paths.Root))
            {
                if (
                    File.Exists(parent)
                    || (
                        documents.TryGetValue(parent, out var planned)
                        && planned.Content is not null
                    )
                )
                {
                    return ToolError.InvalidRequest(
                        $"A file blocks the target directory: {parent}"
                    );
                }

                parent = Path.GetDirectoryName(parent);
            }
        }

        return Result.Success;
    }

    private async Task<Result> PlanOperationAsync(
        DocumentOperation operation,
        Dictionary<string, EditedDocument> documents,
        WorkspaceSnapshot snapshot,
        CancellationToken cancellationToken
    ) =>
        operation switch
        {
            TextDocumentEdit textEdit => await PlanTextEditAsync(
                textEdit,
                documents,
                snapshot,
                cancellationToken
            ),
            CreateFile create => await PlanCreateAsync(
                create,
                documents,
                snapshot,
                cancellationToken
            ),
            RenameFile rename => await PlanRenameAsync(
                rename,
                documents,
                snapshot,
                cancellationToken
            ),
            DeleteFile delete => await PlanDeleteAsync(
                delete,
                documents,
                snapshot,
                cancellationToken
            ),
        };

    private async Task<Result> PlanTextEditAsync(
        TextDocumentEdit operation,
        Dictionary<string, EditedDocument> documents,
        WorkspaceSnapshot snapshot,
        CancellationToken cancellationToken
    )
    {
        var loaded = await GetDocumentAsync(operation.Uri, documents, snapshot, cancellationToken);
        if (loaded.IsError(out var error, out var document))
            return error;

        return
            operation.Version is { } version
            && (
                !snapshot.DocumentVersions.TryGetValue(document.OriginalPath, out var expected)
                || version != expected
            )
            ? ToolError.StaleEdit($"The document version is stale or unknown: {document.Path}")
            : document.Apply(operation.Edits);
    }

    private async Task<Result> PlanCreateAsync(
        CreateFile operation,
        Dictionary<string, EditedDocument> documents,
        WorkspaceSnapshot snapshot,
        CancellationToken cancellationToken
    )
    {
        var loaded = await GetDocumentAsync(operation.Uri, documents, snapshot, cancellationToken);
        if (loaded.IsError(out var error, out var document))
            return error;

        if (document.Content is not null && operation.Options is not { Overwrite: true })
        {
            return operation.Options is { IgnoreIfExists: true }
                ? Result.Success
                : ToolError.InvalidRequest($"The file already exists: {document.Path}");
        }

        document.Content = [];
        return Result.Success;
    }

    private async Task<Result> PlanRenameAsync(
        RenameFile operation,
        Dictionary<string, EditedDocument> documents,
        WorkspaceSnapshot snapshot,
        CancellationToken cancellationToken
    )
    {
        var sourceDocument = await GetDocumentAsync(
            operation.OldUri,
            documents,
            snapshot,
            cancellationToken
        );
        if (sourceDocument.IsError(out var sourceError, out var source))
            return sourceError;

        if (source.Content is null)
            return ToolError.InvalidRequest($"The file to rename does not exist: {source.Path}");

        if (ResolveEditPath(operation.NewUri).IsError(out var pathError, out var destinationPath))
            return pathError;

        if (IsCaseOnlyAlias(source, destinationPath, snapshot))
        {
            source.Path = destinationPath;
            return Result.Success;
        }

        var destinationDocument = await GetDocumentAsync(
            operation.NewUri,
            documents,
            snapshot,
            cancellationToken
        );
        if (destinationDocument.IsError(out var destinationError, out var destination))
            return destinationError;

        if (ReferenceEquals(source, destination))
            return Result.Success;

        if (destination.Content is not null && operation.Options is not { Overwrite: true })
        {
            return operation.Options is { IgnoreIfExists: true }
                ? Result.Success
                : ToolError.InvalidRequest($"The rename target already exists: {destination.Path}");
        }

        destination.Content = source.Content;
        destination.ModeSource = source.ModeSource;
        source.Content = null;
        return Result.Success;
    }

    private static bool IsCaseOnlyAlias(
        EditedDocument source,
        string destination,
        WorkspaceSnapshot snapshot
    ) =>
        !StringComparer.Ordinal.Equals(source.Path, destination)
        && StringComparer.OrdinalIgnoreCase.Equals(source.Path, destination)
        && StringComparer.Ordinal.Equals(
            Path.GetDirectoryName(source.Path),
            Path.GetDirectoryName(destination)
        )
        && File.Exists(destination)
        && (
            !snapshot.Files.ContainsKey(destination)
            || StringComparer.Ordinal.Equals(source.OriginalPath, destination)
        );

    private async Task<Result> PlanDeleteAsync(
        DeleteFile operation,
        Dictionary<string, EditedDocument> documents,
        WorkspaceSnapshot snapshot,
        CancellationToken cancellationToken
    )
    {
        var loaded = await GetDocumentAsync(operation.Uri, documents, snapshot, cancellationToken);
        if (loaded.IsError(out var error, out var document))
            return error;

        if (document.Content is null && operation.Options is not { IgnoreIfNotExists: true })
            return ToolError.InvalidRequest($"The file to delete does not exist: {document.Path}");

        document.Content = null;
        return Result.Success;
    }

    private async Task<Result<EditedDocument>> GetDocumentAsync(
        string uri,
        Dictionary<string, EditedDocument> documents,
        WorkspaceSnapshot snapshot,
        CancellationToken cancellationToken
    )
    {
        if (ResolveEditPath(uri).IsError(out var pathError, out var path))
            return pathError;

        if (documents.TryGetValue(path, out var document))
            return document;

        document = documents.Values.FirstOrDefault(candidate =>
            candidate.CaseOnlyRename && StringComparer.Ordinal.Equals(candidate.Path, path)
        );
        if (document is not null)
            return document;

        if (Directory.Exists(path))
            return ToolError.InvalidRequest($"Workspace edits support files only: {path}");

        var original = await ReadOriginalAsync(path, snapshot, cancellationToken);
        if (original.IsError(out var readError, out var content))
            return readError;

        document = new EditedDocument(path, content);
        documents.Add(path, document);
        return CheckSize(documents.Values) is ToolError sizeError ? sizeError : document;
    }

    private static string DestinationOperation(EditedDocument document) =>
        document.Original is null ? FileOperation.Create : FileOperation.Change;

    private static Result CheckSize(Dictionary<string, EditedDocument>.ValueCollection documents)
    {
        var total = documents.Sum(document =>
            (long)(document.Original?.Length ?? 0)
            + (
                ReferenceEquals(document.Original, document.Content)
                    ? 0
                    : document.Content?.Length ?? 0
            )
        );
        return documents.Count > MaximumEditedFiles || total > MaximumEditBytes
            ? ToolError.InvalidRequest(
                $"The workspace edit exceeds {MaximumEditedFiles} files or {MaximumEditBytes / BytesPerMebibyte} MiB."
            )
            : Result.Success;
    }

    private readonly record struct PendingMutation(string Path, string Operation);

    private sealed record ApplyFailure(ToolError Error, PendingMutation? Mutation);
}

internal sealed record EditPreview(IReadOnlyList<FilePreview> Files, WorkspaceSnapshot Snapshot);
