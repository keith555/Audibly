// Author: rstewa · https://github.com/rstewa
// Created: 04/18/2026

using Audibly.Models;
using Audibly.Repository.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Audibly.Repository.Sql;

public class SqlBookmarkRepository(AudiblyContext db) : IBookmarkRepository
{
    public async Task<IEnumerable<Bookmark>> GetByAudiobookAsync(Guid audiobookId)
    {
        return await db.Bookmarks
            .Where(b => b.AudiobookId == audiobookId)
            .OrderBy(b => b.PositionMs)
            .AsNoTracking()
            .ToListAsync();
    }

    public async Task<IEnumerable<Bookmark>> GetBySourceFileAsync(Guid sourceFileId)
    {
        return await db.Bookmarks
            .Where(b => b.SourceFileId == sourceFileId)
            .OrderBy(b => b.PositionMs)
            .AsNoTracking()
            .ToListAsync();
    }

    public Task<Bookmark?> GetExistingAtPositionAsync(Guid sourceFileId, long positionMs)
    {
        return db.Bookmarks
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.SourceFileId == sourceFileId && b.PositionMs == positionMs);
    }

    public async Task<Bookmark?> UpsertAsync(Bookmark bookmark)
    {
        var current = await db.Bookmarks
            .FirstOrDefaultAsync(b => b.Id == bookmark.Id);

        if (current == null)
            db.Bookmarks.Add(bookmark);
        else
            db.Entry(current).CurrentValues.SetValues(bookmark);

        try
        {
            await db.SaveChangesAsync();
        }
        catch
        {
            return null;
        }

        return bookmark;
    }

    public async Task<int> AddManyAsync(IEnumerable<Bookmark> bookmarks)
    {
        var list = bookmarks.ToList();
        if (list.Count == 0) return 0;

        db.Bookmarks.AddRange(list);
        return await db.SaveChangesAsync();
    }

    public async Task DeleteAsync(Guid bookmarkId)
    {
        var bookmark = await db.Bookmarks.FirstOrDefaultAsync(b => b.Id == bookmarkId);
        if (bookmark == null) return;

        db.Bookmarks.Remove(bookmark);
        await db.SaveChangesAsync();
    }
}
