// Author: rstewa · https://github.com/rstewa
// Created: 04/18/2026

using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Audibly.App.Services;

/// <summary>
///     Parses a Musicolet bookmarks .txt file.
///     Format: each entry starts with a line like <c>[H:MM:SS]</c> followed by one or
///     more lines of note text. Entries are separated by blank lines.
/// </summary>
public static class MusicoletBookmarkParser
{
    private static readonly Regex HeaderRegex =
        new(@"^\[(\d+):(\d{1,2}):(\d{1,2})\]\s*$", RegexOptions.Compiled);

    public record ParsedBookmark(long PositionMs, string Note);

    public static List<ParsedBookmark> Parse(string content)
    {
        var results = new List<ParsedBookmark>();
        if (string.IsNullOrWhiteSpace(content)) return results;

        var lines = content.Replace("\r\n", "\n").Split('\n');

        long? currentPositionMs = null;
        var noteLines = new List<string>();

        void Flush()
        {
            if (currentPositionMs is null) return;

            var note = string.Join("\n", noteLines).Trim();
            results.Add(new ParsedBookmark(currentPositionMs.Value, note));
            currentPositionMs = null;
            noteLines.Clear();
        }

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();
            var match = HeaderRegex.Match(line);

            if (match.Success)
            {
                Flush();
                var h = int.Parse(match.Groups[1].Value);
                var m = int.Parse(match.Groups[2].Value);
                var s = int.Parse(match.Groups[3].Value);
                currentPositionMs = ((h * 60L + m) * 60L + s) * 1000L;
            }
            else if (currentPositionMs is not null)
            {
                noteLines.Add(line);
            }
        }

        Flush();
        return results;
    }
}
