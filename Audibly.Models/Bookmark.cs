// Author: rstewa · https://github.com/rstewa
// Created: 04/18/2026

namespace Audibly.Models;

public class Bookmark : DbObject
{
    public Guid AudiobookId { get; set; }
    public Audiobook Audiobook { get; set; }

    public Guid SourceFileId { get; set; }
    public SourceFile SourceFile { get; set; }

    /// <summary>
    ///     Position within the source file, in milliseconds.
    /// </summary>
    public long PositionMs { get; set; }

    public string Note { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
