// Author: rstewa · https://github.com/rstewa
// Created: 04/18/2026

using Audibly.Models;

namespace Audibly.Repository.Interfaces;

public interface IBookmarkRepository
{
    /// <summary>
    ///     Returns all bookmarks belonging to the given audiobook, ordered by source file index then position.
    /// </summary>
    Task<IEnumerable<Bookmark>> GetByAudiobookAsync(Guid audiobookId);

    /// <summary>
    ///     Returns all bookmarks belonging to the given source file, ordered by position.
    /// </summary>
    Task<IEnumerable<Bookmark>> GetBySourceFileAsync(Guid sourceFileId);

    /// <summary>
    ///     Returns a bookmark at the exact position within the source file, if one exists.
    /// </summary>
    Task<Bookmark?> GetExistingAtPositionAsync(Guid sourceFileId, long positionMs);

    /// <summary>
    ///     Adds or updates a bookmark.
    /// </summary>
    Task<Bookmark?> UpsertAsync(Bookmark bookmark);

    /// <summary>
    ///     Adds many bookmarks in a single transaction.
    /// </summary>
    Task<int> AddManyAsync(IEnumerable<Bookmark> bookmarks);

    /// <summary>
    ///     Deletes a bookmark.
    /// </summary>
    Task DeleteAsync(Guid bookmarkId);
}
