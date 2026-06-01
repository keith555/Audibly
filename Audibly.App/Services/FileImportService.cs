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

        // Group files by parent directory so a folder of per-chapter files imports as one
        // audiobook instead of one entry per file. Folders with a single audio file fall through
        // the existing single-file path unchanged.
        var groups = files
            .GroupBy(file => Path.GetDirectoryName(file) ?? string.Empty)
            .Select(g => g.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray())
            .ToList();
        var numberOfGroups = groups.Count;

        for (var i = 0; i < numberOfGroups; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var group = groups[i];
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
                // Phase 1: capture pre-grouping per-chapter entries and their bookmarks before
                // we touch the database. Only single-file entries that aren't currently playing
                // are eligible — existing multi-file books are left alone, and we never disrupt
                // active playback mid-sync.
                var stalePerChapter = new List<(Audiobook stale, List<Bookmark> bookmarks)>();
                foreach (var filePath in group)
                {
                    var stale = await App.Repository.Audiobooks.GetByFilePathAsync(filePath);
                    if (stale == null || stale.SourcePaths.Count != 1 || stale.IsNowPlaying) continue;

                    var bookmarks = (await App.Repository.Bookmarks.GetByAudiobookAsync(stale.Id)).ToList();
                    stalePerChapter.Add((stale, bookmarks));
                }

                audiobook = await CreateAudiobookFromMultipleFiles(group);

                if (audiobook != null)
                {
                    var existing = await App.Repository.Audiobooks.GetByTitleAuthorComposerAsync(
                        audiobook.Title, audiobook.Author, audiobook.Composer);
                    if (existing != null)
                    {
                        // Dedup collision — leave the captured per-chapter entries intact rather
                        // than blasting them, since we're not actually replacing them.
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
                    else if (stalePerChapter.Count > 0)
                    {
                        // Transfer bookmarks: for each stale single-file entry, re-create its
                        // bookmarks against the matching SourceFile on the new combined entry,
                        // matched by FilePath (stable across regrouping). PositionMs is an
                        // offset within the audio file, so it carries over verbatim. Preserve
                        // the original CreatedAt so user-visible timestamps don't reset.
                        var newSourceByPath = audiobook.SourcePaths
                            .ToDictionary(sf => sf.FilePath, StringComparer.OrdinalIgnoreCase);

                        var transferred = new List<Bookmark>();
                        foreach (var (stale, bookmarks) in stalePerChapter)
                        {
                            var stalePath = stale.SourcePaths[0].FilePath;
                            if (!newSourceByPath.TryGetValue(stalePath, out var newSf)) continue;

                            foreach (var b in bookmarks)
                                transferred.Add(new Bookmark
                                {
                                    AudiobookId = audiobook.Id,
                                    SourceFileId = newSf.Id,
                                    PositionMs = b.PositionMs,
                                    Note = b.Note,
                                    CreatedAt = b.CreatedAt
                                });
                        }

                        if (transferred.Count > 0)
                            await App.Repository.Bookmarks.AddManyAsync(transferred);

                        // Now remove the stale per-chapter entries. The FK cascade also drops
                        // the original bookmark rows — fine, we've already duplicated them
                        // onto the new entry.
                        foreach (var (stale, _) in stalePerChapter)
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

    private static async Task<Audiobook?> CreateAudiobookFromMultipleFiles(string[] paths)
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
                    // doesn't end up labelled "Chapter 1" in the library.
                    audiobook.Title = string.IsNullOrWhiteSpace(track.Album) ? track.Title : track.Album;
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
}