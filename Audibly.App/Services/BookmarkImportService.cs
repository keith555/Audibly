// Author: rstewa · https://github.com/rstewa
// Created: 04/18/2026

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Audibly.App.Services.Interfaces;
using Audibly.App.Views.ContentDialogs;
using Audibly.Models;

namespace Audibly.App.Services;

public class BookmarkImportService : IBookmarkImportService
{
    /// <summary>
    ///     Matches a trailing " (N)" duplicate marker, e.g. "Foo (2)" → captures the suffix so the
    ///     base name "Foo" is used for source-file matching.
    /// </summary>
    private static readonly Regex DuplicateSuffixRegex = new(@"\s*\(\d+\)$", RegexOptions.Compiled);

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

        // Group input files by their normalized base name so duplicates "(2)", "(3)" etc. merge
        // into one logical import for the same source file.
        var groups = files
            .GroupBy(f => NormalizeKey(Path.GetFileNameWithoutExtension(f.Name)),
                StringComparer.OrdinalIgnoreCase)
            .ToList();

        BookmarkConflictChoice? stickyChoice = null;

        for (var gi = 0; gi < groups.Count; gi++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var group = groups[gi];
            var groupFiles = group.ToList();
            await progressCallback(gi, groups.Count, groupFiles[0].Name);

            if (!sourceIndex.TryGetValue(group.Key, out var match))
            {
                report.UnmatchedFiles += groupFiles.Count;
                foreach (var f in groupFiles) report.UnmatchedFileNames.Add(f.Name);
                continue;
            }

            // Parse every file in the group and amalgamate by position (later file wins on collision).
            var combined = new Dictionary<long, MusicoletBookmarkParser.ParsedBookmark>();
            var readFailed = 0;

            foreach (var file in groupFiles)
            {
                string text;
                try
                {
                    text = await FileIO.ReadTextAsync(file);
                }
                catch
                {
                    readFailed++;
                    report.UnmatchedFileNames.Add(file.Name);
                    continue;
                }

                foreach (var entry in MusicoletBookmarkParser.Parse(text))
                    combined[entry.PositionMs] = entry;
            }

            report.UnmatchedFiles += readFailed;
            var matchedInGroup = groupFiles.Count - readFailed;

            if (combined.Count == 0)
            {
                report.MatchedFiles += matchedInGroup;
                continue;
            }

            var existing = (await App.Repository.Bookmarks.GetBySourceFileAsync(match.sourceFile.Id)).ToList();

            if (existing.Count > 0)
            {
                var promptName = groupFiles.Count == 1
                    ? groupFiles[0].Name
                    : $"{group.Key} ({groupFiles.Count} files)";
                var mergeOk = await mergePrompt(promptName, existing.Count);
                if (!mergeOk)
                {
                    report.SkippedFiles += matchedInGroup;
                    continue;
                }
            }

            report.MatchedFiles += matchedInGroup;

            var existingByPosition = existing.ToDictionary(b => b.PositionMs);
            var toAdd = new List<Bookmark>();
            var toReplace = new List<Bookmark>();

            foreach (var entry in combined.Values.OrderBy(e => e.PositionMs))
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

        await progressCallback(groups.Count, groups.Count, string.Empty);
        return report;
    }

    /// <summary>
    ///     Strips a trailing duplicate marker (e.g. " (2)") so "Foo (2)" matches the source file named "Foo".
    /// </summary>
    private static string NormalizeKey(string name) =>
        string.IsNullOrEmpty(name) ? name : DuplicateSuffixRegex.Replace(name, string.Empty);

    private static string FormatPosition(long ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t:mm\\:ss}"
            : $"{t:mm\\:ss}";
    }
}
