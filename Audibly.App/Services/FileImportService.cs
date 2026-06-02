// Author: rstewa · https://github.com/rstewa
// Created: 04/15/2024
// Updated: 10/17/2024

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using ATL;
using Audibly.App.Extensions;
using Audibly.App.Services.Interfaces;
using Audibly.App.ViewModels;
using Audibly.Models;
using AutoMapper;
using Microsoft.UI.Xaml.Controls;
using Sharpener.Extensions;
using ChapterInfo = Audibly.Models.ChapterInfo;

namespace Audibly.App.Services;

public class FileImportService : IImportFiles
{
    private static IMapper _mapper;

    public FileImportService()
    {
        _mapper = new MapperConfiguration(cfg => { cfg.CreateMap<ATL.ChapterInfo, ChapterInfo>(); }).CreateMapper();
    }

    #region IImportFiles Members

    public event IImportFiles.ImportCompletedHandler? ImportCompleted;

    // TODO: need a better way of checking if a file is one we have already imported
    public async Task ImportDirectoryAsync(string path, CancellationToken cancellationToken,
        Func<int, int, string, bool, Task> progressCallback, bool notifyUser = true)
    {
        var files = Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories)
            .Where(file => file.EndsWith(".m4b", StringComparison.OrdinalIgnoreCase) ||
                           file.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Group files into per-audiobook units. Each folder of audio files becomes a group;
        // additionally a "book root with disc subfolders" layout (multiple sibling subdirs of
        // audio that share an Artist and a non-trivial common Album prefix) is detected and
        // merged into one group. titleOverride is the derived merged title for multi-disc
        // groups; null for ordinary single-folder groups.
        var groups = GroupAudioFilesForImport(files);
        var numberOfGroups = groups.Count;

        for (var i = 0; i < numberOfGroups; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (group, titleOverride) = groups[i];
            var didFail = false;
            Audiobook? audiobook;

            if (group.Length == 1)
            {
                audiobook = await CreateAudiobook(group[0], notifyUser: notifyUser);

                if (audiobook == null)
                {
                    didFail = true;
                }
                else
                {
                    var result = await App.Repository.Audiobooks.UpsertAsync(audiobook);
                    if (result == null) didFail = true;
                }
            }
            else
            {
                // Phase 1: capture stale audiobooks that this new combined entry fully
                // supersedes, along with their bookmarks, before we touch the database. A stale
                // entry is eligible if *every* one of its source paths is in the new group —
                // covers both old per-chapter single-file imports (1 path) and old per-disc
                // multi-file imports (25-ish paths). Currently playing entries and entries
                // whose source paths extend beyond this group are left untouched.
                var newPathSet = new HashSet<string>(group, StringComparer.OrdinalIgnoreCase);
                var staleSuperseded = new List<(Audiobook stale, List<Bookmark> bookmarks)>();
                var seenStaleIds = new HashSet<Guid>();
                foreach (var filePath in group)
                {
                    var stale = await App.Repository.Audiobooks.GetByFilePathAsync(filePath);
                    if (stale == null || stale.IsNowPlaying) continue;
                    if (!seenStaleIds.Add(stale.Id)) continue;
                    if (!stale.SourcePaths.All(sp => newPathSet.Contains(sp.FilePath))) continue;

                    var bookmarks = (await App.Repository.Bookmarks.GetByAudiobookAsync(stale.Id)).ToList();
                    staleSuperseded.Add((stale, bookmarks));
                }

                audiobook = await CreateAudiobookFromMultipleFiles(group, titleOverride);

                if (audiobook != null)
                {
                    var existing = await App.Repository.Audiobooks.GetByTitleAuthorComposerAsync(
                        audiobook.Title, audiobook.Author, audiobook.Composer);
                    if (existing != null && !seenStaleIds.Contains(existing.Id))
                    {
                        // Genuine dedup collision (not one of the stale entries we're about to
                        // replace). Leave everything intact.
                        App.ViewModel.LoggingService.LogError(
                            new Exception("Audiobook already exists in the database"));
                        if (notifyUser)
                            App.ViewModel.EnqueueNotification(new Notification
                            {
                                Message = $"Audiobook is already in the library: {existing.Title}",
                                Severity = InfoBarSeverity.Warning
                            });
                        audiobook = null;
                    }
                }

                if (audiobook == null)
                {
                    didFail = true;
                }
                else
                {
                    // Persist the new combined audiobook first so its (and its SourceFiles')
                    // Ids are assigned, which we need to re-key bookmarks against.
                    var result = await App.Repository.Audiobooks.UpsertAsync(audiobook);
                    if (result == null)
                    {
                        didFail = true;
                    }
                    else if (staleSuperseded.Count > 0)
                    {
                        // Transfer bookmarks: for each stale bookmark, find the stale SourceFile
                        // it lives on (by SourceFileId), then map to the new SourceFile with the
                        // same FilePath. PositionMs is an offset within the audio file so it
                        // carries over verbatim. Preserve CreatedAt so user-visible timestamps
                        // don't reset.
                        var newSourceByPath = audiobook.SourcePaths
                            .ToDictionary(sf => sf.FilePath, StringComparer.OrdinalIgnoreCase);

                        var transferred = new List<Bookmark>();
                        foreach (var (stale, bookmarks) in staleSuperseded)
                        {
                            var staleSourceById = stale.SourcePaths.ToDictionary(sf => sf.Id);
                            foreach (var b in bookmarks)
                            {
                                if (!staleSourceById.TryGetValue(b.SourceFileId, out var staleSf)) continue;
                                if (!newSourceByPath.TryGetValue(staleSf.FilePath, out var newSf)) continue;

                                transferred.Add(new Bookmark
                                {
                                    AudiobookId = audiobook.Id,
                                    SourceFileId = newSf.Id,
                                    PositionMs = b.PositionMs,
                                    Note = b.Note,
                                    CreatedAt = b.CreatedAt
                                });
                            }
                        }

                        if (transferred.Count > 0)
                            await App.Repository.Bookmarks.AddManyAsync(transferred);

                        // Remove the superseded entries. The FK cascade also drops the original
                        // bookmark rows — fine, we've already duplicated them onto the new entry.
                        foreach (var (stale, _) in staleSuperseded)
                        {
                            await App.Repository.Audiobooks.DeleteAsync(stale.Id);
                            await App.ViewModel.AppDataService.DeleteCoverImageAsync(stale.CoverImagePath);
                        }
                    }
                }
            }

            var title = audiobook?.Title ?? Path.GetFileNameWithoutExtension(group[0]);
            await progressCallback(i, numberOfGroups, title, didFail);
        }

        ImportCompleted?.Invoke();
    }

    public async Task ImportFromJsonAsync(StorageFile file, CancellationToken cancellationToken,
        Func<int, int, string, bool, Task> progressCallback)
    {
        // read the json string from the file
        var json = FileIO.ReadTextAsync(file).AsTask().Result;

        if (string.IsNullOrEmpty(json))
        {
            // log the error
            App.ViewModel.LoggingService.LogError(new Exception("Failed to read the json file"), true);
            ImportCompleted?.Invoke();
            return;
        }

        // deserialize the json string to a list of audiobooks
        var importedAudiobooks = JsonSerializer.Deserialize<List<ImportedAudiobook>>(json);

        if (importedAudiobooks == null)
        {
            // log the error
            App.ViewModel.LoggingService.LogError(new Exception("Failed to deserialize the json file"), true);
            return;
        }

        var didFail = false;
        var numberOfFiles = importedAudiobooks.Count;

        foreach (var importedAudiobook in importedAudiobooks)
        {
            // Check if cancellation was requested
            cancellationToken.ThrowIfCancellationRequested();

            // verify that the audiobook file exists
            if (!File.Exists(importedAudiobook.FilePath))
            {
                // log the error
                App.ViewModel.LoggingService.LogError(new Exception("Audiobook file does not exist"));
                App.ViewModel.EnqueueNotification(new Notification
                {
                    Message = $"Audiobook file was moved or deleted: {importedAudiobook.FilePath}",
                    Severity = InfoBarSeverity.Warning
                });

                didFail = true;
                continue;
            }

            var audiobook = await CreateAudiobook(importedAudiobook.FilePath, importedAudiobook);

            if (audiobook == null)
            {
                didFail = true;
            }
            else
            {
                // insert the audiobook into the database
                var result = await App.Repository.Audiobooks.UpsertAsync(audiobook);
                if (result == null) didFail = true;
            }

            var title = audiobook?.Title ?? Path.GetFileNameWithoutExtension(importedAudiobook.FilePath);

            // report progress
            await progressCallback(importedAudiobooks.IndexOf(importedAudiobook), numberOfFiles, title, didFail);

            didFail = false;
        }

        ImportCompleted?.Invoke();
    }

    public async Task ImportFromMultipleFilesAsync(string[] paths, CancellationToken cancellationToken,
        Func<int, int, string, bool, Task> progressCallback)
    {
        var didFail = false;

        // todo: need to see if we can call progressCallback from the CreateAudiobook function
        var numberOfFiles = 1; // paths.Length;

        // Check if cancellation was requested
        cancellationToken.ThrowIfCancellationRequested();

        var audiobook = await CreateAudiobookFromMultipleFiles(paths);

        if (audiobook == null) didFail = true;

        if (audiobook != null)
        {
            var existingAudioBook = await App.Repository.Audiobooks.GetByTitleAuthorComposerAsync(audiobook.Title,
                audiobook.Author,
                audiobook.Composer);
            if (existingAudioBook != null)
            {
                // log the error
                App.ViewModel.LoggingService.LogError(new Exception("Audiobook already exists in the database"));
                App.ViewModel.EnqueueNotification(new Notification
                {
                    Message = $"Audiobook is already in the library: {existingAudioBook.Title}",
                    Severity = InfoBarSeverity.Warning
                });

                didFail = true;

                await progressCallback(numberOfFiles, numberOfFiles, audiobook.Title, didFail);

                ImportCompleted?.Invoke();

                return;
            }

            // insert the audiobook into the database
            var result = await App.Repository.Audiobooks.UpsertAsync(audiobook);
            if (result == null) didFail = true;
        }

        var title = audiobook?.Title ?? Path.GetFileNameWithoutExtension(paths.First());

        // report progress
        await progressCallback(numberOfFiles, numberOfFiles, title, didFail);

        ImportCompleted?.Invoke();
    }

    public async Task ImportFileAsync(string path, CancellationToken cancellationToken,
        Func<int, int, string, bool, Task> progressCallback)
    {
        // Check if cancellation was requested
        cancellationToken.ThrowIfCancellationRequested();

        var didFail = false;
        var audiobook = await CreateAudiobook(path);

        if (audiobook == null) didFail = true;

        // insert the audiobook into the database
        if (audiobook != null)
        {
            var result = await App.Repository.Audiobooks.UpsertAsync(audiobook);
            if (result == null) didFail = true;
        }

        var title = audiobook?.Title ?? Path.GetFileNameWithoutExtension(path);

        // report progress
        // NOTE: keeping this bc this function will be used in the future to import 1-to-many files
        await progressCallback(1, 1, title, didFail);

        ImportCompleted?.Invoke();
    }

    #endregion

    private static async Task<Audiobook?> CreateAudiobookFromMultipleFiles(string[] paths,
        string? titleOverride = null)
    {
        try
        {
            var audiobook = new Audiobook
            {
                CurrentSourceFileIndex = 0,
                SourcePaths = [],
                PlaybackSpeed = 1.0,
                Volume = 1.0,
                IsCompleted = false
            };

            var sourceFileIndex = 0;
            var chapterIndex = 0;
            foreach (var path in paths)
            {
                var track = new Track(path);

                // check if this is the 1st file
                if (audiobook.SourcePaths.Count == 0)
                {
                    // Per-chapter mp3 books typically tag Title as "Chapter N" and the actual
                    // book title as Album. Prefer Album when present so the combined entry
                    // doesn't end up labelled "Chapter 1" in the library. A caller-supplied
                    // titleOverride (e.g. the common Album prefix derived for a multi-disc
                    // set) wins over both.
                    audiobook.Title = !string.IsNullOrWhiteSpace(titleOverride)
                        ? titleOverride
                        : string.IsNullOrWhiteSpace(track.Album) ? track.Title : track.Album;
                    audiobook.Composer = track.Composer;
                    audiobook.Author = track.Artist;
                    audiobook.Description =
                        track.Description.IsNullOrEmpty()
                            ? track.Comment.IsNullOrEmpty()
                                ? track.AdditionalFields.TryGetValue("\u00A9des", out var value) ? value : track.Comment
                                : track.Comment
                            : track.Description;
                    audiobook.ReleaseDate = track.Date;
                }

                var sourceFile = new SourceFile
                {
                    Index = sourceFileIndex++,
                    FilePath = path,
                    Duration = track.Duration,
                    CurrentTimeMs = 0
                };

                audiobook.SourcePaths.Add(sourceFile);

                // read in the chapters
                foreach (var ch in track.Chapters)
                {
                    var tmp = _mapper.Map<ChapterInfo>(ch);
                    tmp.Index = chapterIndex++;
                    tmp.ParentSourceFileIndex = sourceFile.Index;
                    audiobook.Chapters.Add(tmp);
                }

                if (track.Chapters.Count == 0)
                    // create a single chapter for the entire book
                    audiobook.Chapters.Add(new ChapterInfo
                    {
                        StartTime = 0,
                        EndTime = Convert.ToUInt32(audiobook.SourcePaths[sourceFileIndex - 1].Duration * 1000),
                        StartOffset = 0,
                        EndOffset = 0,
                        UseOffset = false,
                        Title = track.Title,
                        Index = chapterIndex++,
                        ParentSourceFileIndex = sourceFile.Index
                    });
            }

            // get duration of the entire audiobook
            audiobook.Duration = audiobook.SourcePaths.Sum(x => x.Duration);

            // save the cover image somewhere
            var imageBytes = new Track(paths.First()).EmbeddedPictures.FirstOrDefault()?.PictureData;

            // generate hash from title, author, and composer
            var hash = $"{audiobook.Title}{audiobook.Author}{audiobook.Composer}".GetSha256Hash();

            // todo: do i want to write the metadata to a json file here?
            // write the metadata to a json file
            // await App.ViewModel.AppDataService.WriteMetadataAsync(dir, track);

            (audiobook.CoverImagePath, audiobook.ThumbnailPath) =
                await App.ViewModel.AppDataService.WriteCoverImageAsync(hash, imageBytes);

            // combine the chapters from all the files
            // audiobook.Chapters = audiobook.SourcePaths.SelectMany(x => x.Chapters).ToList();
            audiobook.CurrentChapterIndex = 0;

            return audiobook;
        }
        catch (Exception e)
        {
            // log the error
            App.ViewModel.LoggingService.LogError(e, true);
            return null;
        }
    }

    private static async Task<Audiobook?> CreateAudiobook(string path, ImportedAudiobook? importedAudiobook = null, bool notifyUser = true)
    {
        try
        {
            var track = new Track(path);

            var existingAudioBook =
                await App.Repository.Audiobooks.GetByTitleAuthorComposerAsync(track.Title, track.Artist,
                    track.Composer);
            if (existingAudioBook != null)
            {
                // log the error
                App.ViewModel.LoggingService.LogError(new Exception("Audiobook already exists in the database"));
                if (notifyUser)
                {
                    App.ViewModel.EnqueueNotification(new Notification
                    {
                        Message = "Audiobook is already in the library.",
                        Severity = InfoBarSeverity.Warning
                    });
                }
                return null;
            }

            var sourceFile = new SourceFile
            {
                Index = 0,
                FilePath = path,
                Duration = track.Duration,
                CurrentTimeMs = importedAudiobook?.CurrentTimeMs ?? 0
                // CurrentChapterIndex = 0,
                // Chapters = []
            };

            var audiobook = new Audiobook
            {
                CurrentSourceFileIndex = 0,
                Title = track.Title,
                Composer = track.Composer,
                CurrentChapterIndex = importedAudiobook?.CurrentChapterIndex ?? 0,
                Duration = track.Duration,
                Author = track.Artist,
                Description =
                    track.Description.IsNullOrEmpty()
                        ? track.Comment.IsNullOrEmpty()
                            ? track.AdditionalFields.TryGetValue("\u00A9des", out var value) ? value : track.Comment
                            : track.Comment
                        : track.Description,
                PlaybackSpeed = 1.0,
                Progress = importedAudiobook?.Progress ?? 0,
                ReleaseDate = track.Date,
                Volume = 1.0,
                IsCompleted = importedAudiobook?.IsCompleted ?? false,
                IsNowPlaying = importedAudiobook?.IsNowPlaying ?? false,
                SourcePaths =
                [
                    sourceFile
                ]
            };

            // TODO: check if the audiobook already exists in the database

            // save the cover image somewhere
            var imageBytes = track.EmbeddedPictures.FirstOrDefault()?.PictureData;

            // generate hash from title, author, and composer
            var hash = $"{audiobook.Title}{audiobook.Author}{audiobook.Composer}".GetSha256Hash();

            // write the metadata to a json file
            // todo: is this killing the import time?
            // await App.ViewModel.AppDataService.WriteMetadataAsync(hash, track);

            (audiobook.CoverImagePath, audiobook.ThumbnailPath) =
                await App.ViewModel.AppDataService.WriteCoverImageAsync(hash, imageBytes);

            // var chapters = audiobook.SourcePaths.First().Chapters;

            // read in the chapters
            var chapterIndex = 0;
            foreach (var ch in track.Chapters)
            {
                var tmp = _mapper.Map<ChapterInfo>(ch);
                tmp.Index = chapterIndex++;
                tmp.ParentSourceFileIndex = sourceFile.Index;
                audiobook.Chapters.Add(tmp);
            }

            if (audiobook.Chapters.Count == 0)
                // create a single chapter for the entire book
                audiobook.Chapters.Add(new ChapterInfo
                {
                    StartTime = 0,
                    EndTime = Convert.ToUInt32(audiobook.SourcePaths.First().Duration * 1000),
                    StartOffset = 0,
                    EndOffset = 0,
                    UseOffset = false,
                    Title = audiobook.Title,
                    Index = 0,
                    ParentSourceFileIndex = sourceFile.Index
                });

            return audiobook;
        }
        catch (Exception e)
        {
            // log the error
            App.ViewModel.LoggingService.LogError(e, true);
            return null;
        }
    }

    /// <summary>
    ///     Group a flat list of audio file paths into per-audiobook units. Folders containing
    ///     audio files become one group (the existing single-folder behaviour). On top of that,
    ///     a "book root with disc subfolders" layout is detected and its sibling subfolders are
    ///     merged into one group with a derived title.
    ///
    ///     Detection criteria for a multi-disc book root D:
    ///       - D itself contains no audio files (only the subdirs, plus optional non-audio).
    ///       - D has 2+ immediate subdirs that each contain audio files (recursively, via the
    ///         per-directory groups list).
    ///       - The first track of every subgroup shares an Artist (non-empty, case-insensitive).
    ///       - The Album tags across subgroups share a common prefix of >=3 chars after trim.
    ///
    ///     The merged title is the common Album prefix (which is also a strong signal that this
    ///     really is one book, not unrelated books happening to share an artist). Files are
    ///     ordered using a natural-sort comparer so "Disc 10" comes after "Disc 2".
    /// </summary>
    private static List<(string[] files, string? titleOverride)> GroupAudioFilesForImport(
        IEnumerable<string> files)
    {
        var perDirGroups = files
            .GroupBy(f => Path.GetDirectoryName(f) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(f => f, NaturalStringComparer.Instance).ToArray(),
                StringComparer.OrdinalIgnoreCase);

        var result = new List<(string[] files, string? titleOverride)>();
        var consumed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var clustersByBookRoot = perDirGroups
            .GroupBy(kv => Path.GetDirectoryName(kv.Key) ?? string.Empty, StringComparer.OrdinalIgnoreCase);

        foreach (var cluster in clustersByBookRoot)
        {
            var bookRoot = cluster.Key;
            var siblings = cluster.ToList();

            // Need 2+ sibling subdir groups to be a multi-disc candidate.
            if (siblings.Count < 2) continue;
            // If the candidate book root has its own audio at the same level, this is a mixed
            // layout (loose tracks alongside disc subdirs). Leave it alone — too risky to merge.
            if (perDirGroups.ContainsKey(bookRoot)) continue;

            // Read first-track metadata for each sibling. Bail on any read failure.
            var metas = new List<(string dir, string artist, string album)>(siblings.Count);
            var readOk = true;
            foreach (var sib in siblings)
                try
                {
                    var track = new Track(sib.Value[0]);
                    metas.Add((sib.Key, (track.Artist ?? string.Empty).Trim(),
                        (track.Album ?? string.Empty).Trim()));
                }
                catch
                {
                    readOk = false;
                    break;
                }

            if (!readOk) continue;

            // Gate 1: all siblings share a non-empty Artist.
            var firstArtist = metas[0].artist;
            if (string.IsNullOrEmpty(firstArtist)) continue;
            if (metas.Any(m => !string.Equals(m.artist, firstArtist, StringComparison.OrdinalIgnoreCase)))
                continue;

            // Gate 2: Album tags share a meaningful common prefix. This is what distinguishes a
            // genuine multi-disc set (all Albums begin "Robinson Crusoe ...") from unrelated
            // books that happen to share an Artist.
            var commonAlbumPrefix = LongestCommonPrefixCI(metas.Select(m => m.album).ToList())
                .TrimEnd(' ', '-', '_', '(', ',', '.', ':', '/', '[', '{');
            if (commonAlbumPrefix.Length < 3) continue;

            // Merge: combine all sibling files in natural-sort order of (subdir, then filename).
            var mergedFiles = siblings
                .OrderBy(kv => kv.Key, NaturalStringComparer.Instance)
                .SelectMany(kv => kv.Value)
                .ToArray();

            result.Add((mergedFiles, commonAlbumPrefix));
            foreach (var sib in siblings) consumed.Add(sib.Key);
        }

        // Anything not consumed by multi-disc merging stays as its own per-directory group.
        foreach (var kv in perDirGroups)
        {
            if (consumed.Contains(kv.Key)) continue;
            result.Add((kv.Value, null));
        }

        return result;
    }

    /// <summary>
    ///     Longest common prefix across strings, case-insensitive. Returns characters from the
    ///     first string so casing is preserved.
    /// </summary>
    private static string LongestCommonPrefixCI(IReadOnlyList<string> strs)
    {
        if (strs.Count == 0) return string.Empty;
        var first = strs[0];
        var len = first.Length;
        for (var i = 1; i < strs.Count; i++)
        {
            var s = strs[i];
            var bound = Math.Min(len, s.Length);
            var j = 0;
            while (j < bound && char.ToLowerInvariant(first[j]) == char.ToLowerInvariant(s[j]))
                j++;
            len = j;
            if (len == 0) break;
        }

        return first[..len];
    }

    /// <summary>
    ///     Comparer that orders embedded numeric runs by numeric value, so "Disc 2" sorts before
    ///     "Disc 10". Non-digit runs compare case-insensitively.
    /// </summary>
    private sealed class NaturalStringComparer : IComparer<string>
    {
        public static readonly NaturalStringComparer Instance = new();

        public int Compare(string? a, string? b)
        {
            if (a is null) return b is null ? 0 : -1;
            if (b is null) return 1;

            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
                {
                    while (i < a.Length && a[i] == '0') i++;
                    while (j < b.Length && b[j] == '0') j++;
                    int iStart = i, jStart = j;
                    while (i < a.Length && char.IsDigit(a[i])) i++;
                    while (j < b.Length && char.IsDigit(b[j])) j++;
                    int iLen = i - iStart, jLen = j - jStart;
                    if (iLen != jLen) return iLen.CompareTo(jLen);
                    var cmp = string.CompareOrdinal(a, iStart, b, jStart, iLen);
                    if (cmp != 0) return cmp;
                }
                else
                {
                    var cmp = char.ToLowerInvariant(a[i]).CompareTo(char.ToLowerInvariant(b[j]));
                    if (cmp != 0) return cmp;
                    i++;
                    j++;
                }
            }

            return (a.Length - i).CompareTo(b.Length - j);
        }
    }
}