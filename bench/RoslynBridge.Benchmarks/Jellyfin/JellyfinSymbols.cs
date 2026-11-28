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
    internal const string GET_ITEM_BY_ID =
        "MediaBrowser.Controller.Library.ILibraryManager.GetItemById(Guid)";
    internal const string GET_ITEM_BY_ID_FILE =
        "MediaBrowser.Controller/Library/ILibraryManager.cs";
    internal const int GET_ITEM_BY_ID_LINE = 201;
    internal const int GET_ITEM_BY_ID_CHARACTER = 19;

    // Most referenced BaseItem property (893 refs)
    internal const string BASE_ITEM_ID = "MediaBrowser.Controller.Entities.BaseItem.Id";

    // Longest LibraryManager method (205 lines)
    internal const string DELETE_ITEM =
        "Emby.Server.Implementations.Library.LibraryManager.DeleteItem(BaseItem, DeleteOptions, BaseItem, bool)";

    // Root of the item hierarchy (243 members)
    internal const string BASE_ITEM = "MediaBrowser.Controller.Entities.BaseItem";

    // Largest project (387 files)
    internal const string CONTROLLER_FILES = "MediaBrowser.Controller/**/*.cs";

    // Largest C# file (8,093 lines)
    internal const string ENCODING_HELPER_FILE =
        "MediaBrowser.Controller/MediaEncoding/EncodingHelper.cs";

    // Bare member name (247 declarations)
    internal const string BARE_NAME = "Name";

    // Central service name (160 matching symbols)
    internal const string LIBRARY_MANAGER_QUERY = "LibraryManager";

    // Missing ILibraryManager member (115 methods ranked as suggestions)
    internal const string MISSING_MEMBER =
        "MediaBrowser.Controller.Library.ILibraryManager.GetItem";

    // GetItemById with a typo in its type (similar types ranked as suggestions)
    internal const string MISSPELLED_TYPE =
        "MediaBrowser.Controller.Library.ILibraryManger.GetItemById(Guid)";
}
