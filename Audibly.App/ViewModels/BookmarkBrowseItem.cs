// Author: rstewa · https://github.com/rstewa
// Created: 04/19/2026

using System;
using System.Linq;
using Audibly.Models;

namespace Audibly.App.ViewModels;

/// <summary>
///     Flattened row for the Bookmarks browse page. Combines a <see cref="Bookmark" /> with the
///     human-readable book/chapter/time labels resolved against its parent <see cref="Audiobook" />.
/// </summary>
public class BookmarkBrowseItem : BindableBase
{
    private string _note;

    public BookmarkBrowseItem(Bookmark bookmark, Audiobook audiobook)
    {
        Bookmark = bookmark;
        Audiobook = audiobook;
        _note = bookmark.Note ?? string.Empty;

        BookTitle = audiobook?.Title ?? "(unknown book)";
        BookAuthor = audiobook?.Author ?? string.Empty;
        Location = ResolveLocation(bookmark, audiobook);
    }

    public Bookmark Bookmark { get; }
    public Audiobook Audiobook { get; }

    public string BookTitle { get; }
    public string BookAuthor { get; }
    public string Location { get; }

    public string Note
    {
        get => _note;
        set
        {
            if (Set(ref _note, value))
                Bookmark.Note = value;
        }
    }

    private static string ResolveLocation(Bookmark bookmark, Audiobook audiobook)
    {
        if (audiobook == null) return FormatMs(bookmark.PositionMs);

        var sourceFile = audiobook.SourcePaths?.FirstOrDefault(s => s.Id == bookmark.SourceFileId);
        var chapter = sourceFile == null
            ? null
            : audiobook.Chapters?.FirstOrDefault(c =>
                c.ParentSourceFileIndex == sourceFile.Index &&
                bookmark.PositionMs >= c.StartTime &&
                bookmark.PositionMs <= c.EndTime);

        var time = FormatMs(bookmark.PositionMs);
        return chapter != null ? $"{chapter.Title} • {time}" : time;
    }

    private static string FormatMs(long ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t:mm\\:ss}"
            : $"{t:mm\\:ss}";
    }
}
