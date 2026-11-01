// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace RoslynBridge.Benchmarks.Jellyfin;

/// <summary>
/// Benchmark inputs in Jellyfin v12.4.
/// </summary>
/// <remarks>
/// Each is the heaviest case of a central type. Counts come from the bridge.
/// </remarks>
internal static class JellyfinSymbols
{
    // Most referenced ILibraryManager method (152 refs)
    internal const string GetItemById =
        "MediaBrowser.Controller.Library.ILibraryManager.GetItemById(Guid)";
    internal const string GetItemByIdFile = "MediaBrowser.Controller/Library/ILibraryManager.cs";
    internal const int GetItemByIdLine = 201;
    internal const int GetItemByIdCharacter = 19;

    // Most referenced BaseItem property (893 refs)
    internal const string BaseItemId = "MediaBrowser.Controller.Entities.BaseItem.Id";

    // Longest LibraryManager method (205 lines)
    internal const string DeleteItem =
        "Emby.Server.Implementations.Library.LibraryManager.DeleteItem(BaseItem, DeleteOptions, BaseItem, bool)";

    // Root of the item hierarchy (243 members)
    internal const string BaseItem = "MediaBrowser.Controller.Entities.BaseItem";

    // Largest project (387 files)
    internal const string ControllerFiles = "MediaBrowser.Controller/**/*.cs";

    // Largest C# file (8,093 lines)
    internal const string EncodingHelperFile =
        "MediaBrowser.Controller/MediaEncoding/EncodingHelper.cs";

    // Bare member name (247 declarations)
    internal const string BareName = "Name";

    // Central service name (160 matching symbols)
    internal const string LibraryManagerQuery = "LibraryManager";

    // Missing ILibraryManager member (115 methods ranked as suggestions)
    internal const string MissingMember = "MediaBrowser.Controller.Library.ILibraryManager.GetItem";

    // GetItemById with a typo in its type (similar types ranked as suggestions)
    internal const string MisspelledType =
        "MediaBrowser.Controller.Library.ILibraryManger.GetItemById(Guid)";
}
