using Common.Entities.CopilotAdoption;
using System.Collections.Generic;

namespace Common.Entities.TeamsExplorer
{
    /// <summary>
    /// CSV column definitions for the Teams Explorer's tabular sections.
    /// </summary>
    /// <remarks>
    /// Reuses <see cref="CsvSerialiser"/> rather than formatting CSV here. That is deliberate: it
    /// already handles the three things that silently ruin an exported user list - a UTF-8 BOM so
    /// Excel renders non-Latin team and department names instead of mojibake, neutralised formula
    /// injection for values that come from the customer's own directory, and invariant number and
    /// date formatting. Re-implementing any of that would reintroduce bugs that are already fixed.
    /// </remarks>
    public static class TeamsExplorerExports
    {
        /// <summary>The sections that can be exported, as their URL segment.</summary>
        public static readonly string[] Sections = { "people", "dormant", "teams", "channels", "adoption" };

        /// <summary>True when <paramref name="section"/> names a real export.</summary>
        public static bool IsKnownSection(string section)
        {
            if (string.IsNullOrEmpty(section)) return false;

            foreach (var known in Sections)
            {
                if (string.Equals(known, section, System.StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }

        /// <summary>
        /// Champions and dormant users share a column set. The two files differ only in which rows
        /// they carry, so giving them different shapes would make them impossible to compare.
        /// </summary>
        public static IReadOnlyList<CsvColumn<TeamsPersonRow>> PersonColumns()
        {
            return new List<CsvColumn<TeamsPersonRow>>
            {
                new CsvColumn<TeamsPersonRow>("User principal name", r => r.UserPrincipalName),
                new CsvColumn<TeamsPersonRow>("Department", r => r.Department),
                new CsvColumn<TeamsPersonRow>("Engagement segment", r => r.Segment),
                new CsvColumn<TeamsPersonRow>("Active days in period", r => r.ActiveDays),
                new CsvColumn<TeamsPersonRow>("Channel messages", r => r.ChannelMessages),
                new CsvColumn<TeamsPersonRow>("Private chat messages", r => r.PrivateMessages),
                new CsvColumn<TeamsPersonRow>("Meetings organised", r => r.MeetingsOrganised),
                new CsvColumn<TeamsPersonRow>("Meetings attended", r => r.MeetingsAttended),
                new CsvColumn<TeamsPersonRow>("Calls hosted", r => r.CallsHosted),
                new CsvColumn<TeamsPersonRow>("Calls attended", r => r.CallsAttended),
                new CsvColumn<TeamsPersonRow>("Last Teams activity (UTC)", r => r.LastActivity),
            };
        }

        /// <summary>
        /// The team leaderboard. "Authorised for deep analytics" is included because without it a
        /// zero in the messages column is ambiguous - an unauthorised team is unmeasured, not quiet,
        /// and archiving one on that basis would be a mistake.
        /// </summary>
        public static IReadOnlyList<CsvColumn<TeamsTeamRow>> TeamColumns()
        {
            return new List<CsvColumn<TeamsTeamRow>>
            {
                new CsvColumn<TeamsTeamRow>("Team", r => r.Name),
                new CsvColumn<TeamsTeamRow>("Authorised for deep analytics", r => r.Authorised),
                new CsvColumn<TeamsTeamRow>("Members", r => r.Members),
                new CsvColumn<TeamsTeamRow>("Owners", r => r.Owners),
                new CsvColumn<TeamsTeamRow>("Channels", r => r.Channels),
                new CsvColumn<TeamsTeamRow>("Tabs in use", r => r.Tabs),
                new CsvColumn<TeamsTeamRow>("Channel messages", r => r.Messages),
                new CsvColumn<TeamsTeamRow>("Reactions", r => r.Reactions),
                new CsvColumn<TeamsTeamRow>("Days with a message", r => r.ActiveDays),
                // Named with its scale in the heading. A bare "Sentiment" column of 0.5s reads as
                // "50% positive", which is wrong - 0.5 is exactly neutral on this scale.
                new CsvColumn<TeamsTeamRow>("Sentiment (0 negative, 0.5 neutral, 1 positive)", r => r.Sentiment),
            };
        }

        /// <summary>The channel leaderboard.</summary>
        public static IReadOnlyList<CsvColumn<TeamsChannelRow>> ChannelColumns()
        {
            return new List<CsvColumn<TeamsChannelRow>>
            {
                new CsvColumn<TeamsChannelRow>("Team", r => r.TeamName),
                new CsvColumn<TeamsChannelRow>("Channel", r => r.Name),
                new CsvColumn<TeamsChannelRow>("Tabs in use", r => r.Tabs),
                new CsvColumn<TeamsChannelRow>("Channel messages", r => r.Messages),
                new CsvColumn<TeamsChannelRow>("Reactions", r => r.Reactions),
                new CsvColumn<TeamsChannelRow>("People reacting", r => r.ReactingUsers),
                new CsvColumn<TeamsChannelRow>("Days with a message", r => r.ActiveDays),
                new CsvColumn<TeamsChannelRow>("Sentiment (0 negative, 0.5 neutral, 1 positive)", r => r.Sentiment),
            };
        }

        /// <summary>The adoption breakdown for whichever demographic the page is grouped by.</summary>
        public static IReadOnlyList<CsvColumn<TeamsDemographicRow>> DemographicColumns(string groupBy)
        {
            var heading = GroupingHeading(groupBy);

            return new List<CsvColumn<TeamsDemographicRow>>
            {
                new CsvColumn<TeamsDemographicRow>(heading, r => r.Name),
                new CsvColumn<TeamsDemographicRow>("Known users", r => r.KnownUsers),
                new CsvColumn<TeamsDemographicRow>("Active users", r => r.ActiveUsers),
                new CsvColumn<TeamsDemographicRow>("Reach (%)", r => r.ReachPct),
                new CsvColumn<TeamsDemographicRow>("Messages per active user", r => r.MessagesPerActiveUser),
                new CsvColumn<TeamsDemographicRow>("Meetings per active user", r => r.MeetingsPerActiveUser),
            };
        }

        /// <summary>The human-readable name of a demographic dimension, for a column heading or a label.</summary>
        public static string GroupingHeading(string groupBy)
        {
            switch (TeamsExplorerQuery.NormaliseGrouping(groupBy))
            {
                case "country": return "Country";
                case "office": return "Office";
                case "jobTitle": return "Job title";
                case "company": return "Company";
                default: return "Department";
            }
        }

        /// <summary>The download file name prefix for a section.</summary>
        public static string FileNamePrefix(string section)
        {
            switch ((section ?? string.Empty).ToLowerInvariant())
            {
                case "dormant": return "teams-dormant-users";
                case "teams": return "teams-leaderboard";
                case "channels": return "teams-channel-leaderboard";
                case "adoption": return "teams-adoption-breakdown";
                default: return "teams-champions";
            }
        }
    }
}
