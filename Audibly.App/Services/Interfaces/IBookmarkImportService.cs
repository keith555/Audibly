// Author: rstewa · https://github.com/rstewa
// Created: 04/18/2026

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Audibly.App.Views.ContentDialogs;

namespace Audibly.App.Services.Interfaces;

public interface IBookmarkImportService
{
    /// <summary>
    ///     Prompts the user when bookmarks already exist for a source file. Returns true to merge, false to skip.
    /// </summary>
    public delegate Task<bool> MergePromptHandler(string sourceFileName, int existingCount);

    /// <summary>
    ///     Imports Musicolet bookmark .txt files. Each file is matched against an existing source file by
    ///     filename (without extension). Bookmarks for matched source files are added; existing bookmarks
    ///     at the same position trigger a per-conflict prompt via <paramref name="conflictResolver"/>.
    /// </summary>
    Task<BookmarkImportReport> ImportMusicoletAsync(
        IReadOnlyList<StorageFile> files,
        CancellationToken cancellationToken,
        Func<int, int, string, Task> progressCallback,
        MergePromptHandler mergePrompt,
        Func<string, string, string, Task<(BookmarkConflictChoice choice, bool applyToAll)>>
            conflictResolver);
}

public class BookmarkImportReport
{
    public int MatchedFiles { get; set; }
    public int UnmatchedFiles { get; set; }
    public int SkippedFiles { get; set; }
    public int BookmarksAdded { get; set; }
    public int BookmarksReplaced { get; set; }
    public int BookmarksSkipped { get; set; }
    public List<string> UnmatchedFileNames { get; } = new();
}
