// Author: rstewa · https://github.com/rstewa
// Created: 04/18/2026

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Audibly.App.Services.Interfaces;
using Audibly.App.Views.ContentDialogs;
using Audibly.Models;

namespace Audibly.App.Services;

public class BookmarkImportService : IBookmarkImportService
{
    public async Task<BookmarkImportReport> ImportMusicoletAsync(
        IReadOnlyList<StorageFile> files,
        CancellationToken cancellationToken,
        Func<int, int, string, Task> progressCallback,
        IBookmarkImportService.MergePromptHandler mergePrompt,
        Func<string, string, string, Task<(BookmarkConflictChoice choice, bool applyToAll)>> conflictResolver)
    {
        var report = new BookmarkImportReport();

        // Build an index of all source files by basename (case-insensitive) across the library.
        var audiobooks = (await App.Repository.Audiobooks.GetAsync()).ToList();
        var sourceIndex = new Dictionary<string, (Audiobook audiobook, SourceFile sourceFile)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var ab in audiobooks)
        foreach (var sf in ab.SourcePaths)
        {
            var key = Path.GetFileNameWithoutExtension(sf.FilePath);
            if (!string.IsNullOrEmpty(key))
                sourceIndex[key] = (ab, sf);
        }

        BookmarkConflictChoice? stickyChoice = null;

        for (var i = 0; i < files.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var file = files[i];
            await progressCallback(i, files.Count, file.Name);

            var key = Path.GetFileNameWithoutExtension(file.Name);

            if (!sourceIndex.TryGetValue(key, out var match))
            {
                report.UnmatchedFiles++;
                report.UnmatchedFileNames.Add(file.Name);
                continue;
            }

            string text;
            try
            {
                text = await FileIO.ReadTextAsync(file);
            }
            catch
            {
                report.UnmatchedFiles++;
                report.UnmatchedFileNames.Add(file.Name);
                continue;
            }

            var parsed = MusicoletBookmarkParser.Parse(text);
            if (parsed.Count == 0)
            {
                report.MatchedFiles++;
                continue;
            }

            var existing = (await App.Repository.Bookmarks.GetBySourceFileAsync(match.sourceFile.Id)).ToList();

            if (existing.Count > 0)
            {
                var mergeOk = await mergePrompt(file.Name, existing.Count);
                if (!mergeOk)
                {
                    report.SkippedFiles++;
                    continue;
                }
            }

            report.MatchedFiles++;

            var existingByPosition = existing.ToDictionary(b => b.PositionMs);
            var toAdd = new List<Bookmark>();
            var toReplace = new List<Bookmark>();

            foreach (var entry in parsed)
            {
                if (!existingByPosition.TryGetValue(entry.PositionMs, out var dup))
                {
                    toAdd.Add(new Bookmark
                    {
                        AudiobookId = match.audiobook.Id,
                        SourceFileId = match.sourceFile.Id,
                        PositionMs = entry.PositionMs,
                        Note = entry.Note
                    });
                    continue;
                }

                var choice = stickyChoice;
                if (choice is null)
                {
                    var location = FormatPosition(entry.PositionMs);
                    var (userChoice, applyToAll) = await conflictResolver(location, dup.Note, entry.Note);
                    choice = userChoice;
                    if (applyToAll) stickyChoice = userChoice;
                }

                switch (choice)
                {
                    case BookmarkConflictChoice.KeepExisting:
                        report.BookmarksSkipped++;
                        break;
                    case BookmarkConflictChoice.Replace:
                        dup.Note = entry.Note;
                        toReplace.Add(dup);
                        break;
                    case BookmarkConflictChoice.KeepBoth:
                        toAdd.Add(new Bookmark
                        {
                            AudiobookId = match.audiobook.Id,
                            SourceFileId = match.sourceFile.Id,
                            PositionMs = entry.PositionMs,
                            Note = entry.Note
                        });
                        break;
                }
            }

            if (toAdd.Count > 0)
            {
                await App.Repository.Bookmarks.AddManyAsync(toAdd);
                report.BookmarksAdded += toAdd.Count;
            }

            foreach (var updated in toReplace)
            {
                await App.Repository.Bookmarks.UpsertAsync(updated);
                report.BookmarksReplaced++;
            }
        }

        await progressCallback(files.Count, files.Count, string.Empty);
        return report;
    }

    private static string FormatPosition(long ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t:mm\\:ss}"
            : $"{t:mm\\:ss}";
    }
}
