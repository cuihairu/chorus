using System;
using System.Collections.Generic;

namespace Chorus.UnitySdk
{
    /// <summary>The RANK-01 subject types a board counts (FIX-U3D-13). The wire carries the
    /// number; a wrong value reads an empty board rather than another board's rows — the
    /// subject type is the row filter.</summary>
    public enum LeaderboardSubject
    {
        Player = 0,
        Streamer = 1,
        Faction = 2,
    }

    /// <summary>One leaderboard's read key (FIX-U3D-13): the board id plus the period bucket
    /// and scope that name one distinct board. An empty period reads the all-time bucket and
    /// an empty scope reads the global scope — the write side's descriptors (aggregation,
    /// bounds, the period calendar) shape scoring, not reads, so the client never supplies
    /// them. The tenant and the game are the session's own facts; the SDK never sends them.</summary>
    public sealed class LeaderboardKey
    {
        /// <summary>The board the game declared, as the scoring side named it.</summary>
        public string BoardId { get; set; } = string.Empty;

        /// <summary>The period bucket id (RANK-07 names buckets like "daily-2026-11-05");
        /// null or empty reads the all-time bucket.</summary>
        public string? PeriodId { get; set; }

        /// <summary>The scope id; null or empty reads the global scope.</summary>
        public string? ScopeId { get; set; }

        /// <summary>The board's subject type — the row filter.</summary>
        public LeaderboardSubject Subject { get; set; } = LeaderboardSubject.Player;
    }

    /// <summary>One entry of a TopN page: a subject and its aggregated score, in the board's
    /// declared order (score descending, subject id ascending).</summary>
    public sealed class LeaderboardEntry
    {
        public string SubjectId { get; set; } = string.Empty;
        public long Score { get; set; }
    }

    /// <summary>One TopN page at one read view: the read version is the snapshot the page
    /// belongs to — a continuation carries it back and a changed board is a stale read
    /// (restart from the first page, never stitch across versions).</summary>
    public sealed class LeaderboardTop
    {
        public IReadOnlyList<LeaderboardEntry> Entries { get; set; }
            = Array.Empty<LeaderboardEntry>();

        /// <summary>The board's read version at this page's snapshot (empty when the board
        /// has never scored).</summary>
        public string ReadVersion { get; set; } = string.Empty;

        /// <summary>The next page's cursor, present exactly when <see cref="HasMore"/>;
        /// pass it back to <see cref="ChorusSession.GetTopAsync"/> to continue the walk.</summary>
        public string? NextCursor { get; set; }

        public bool HasMore { get; set; }
    }

    /// <summary>One subject's position at one read view: its unique ordinal rank and its
    /// score, never from different versions. <see cref="Found"/> false is an answer — the
    /// subject has not scored on the board — not an error.</summary>
    public sealed class LeaderboardRank
    {
        public bool Found { get; set; }
        public long Rank { get; set; }
        public string SubjectId { get; set; } = string.Empty;
        public long Score { get; set; }
        public string ReadVersion { get; set; } = string.Empty;
    }

    /// <summary>One ranked entry of an around window.</summary>
    public sealed class LeaderboardRankedEntry
    {
        /// <summary>The subject's unique ordinal in the board's order (ties take
        /// consecutive ranks).</summary>
        public long Rank { get; set; }
        public string SubjectId { get; set; } = string.Empty;
        public long Score { get; set; }
    }

    /// <summary>The entries within a bounded window of a subject's rank (the subject
    /// included), in rank order, at one read view.</summary>
    public sealed class LeaderboardAround
    {
        public bool Found { get; set; }
        public string ReadVersion { get; set; } = string.Empty;
        public IReadOnlyList<LeaderboardRankedEntry> Entries { get; set; }
            = Array.Empty<LeaderboardRankedEntry>();
    }

    /// <summary>A continuation whose board changed or whose snapshot lifetime expired
    /// (RANK-06's bounded walk). The answer carries no page: restart the walk from the
    /// first cursor-less call — the walk never stitches pages across versions.</summary>
    public class LeaderboardStaleException : Exception
    {
        public LeaderboardStaleException(string message) : base(message) { }
    }
}
