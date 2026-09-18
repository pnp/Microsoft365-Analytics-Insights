using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Entity;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using static Common.Entities.TeamsExplorer.TeamsExplorerRows;

namespace Common.Entities.TeamsExplorer
{
    /// <summary>Loads the Teams Explorer's sections.</summary>
    public interface ITeamsExplorerStore
    {
        /// <summary>Team counts for the availability model - null when they could not be read.</summary>
        Task<Tuple<int, int>> GetTeamCountsAsync();

        Task<TeamsOverview> GetOverviewAsync(TeamsExplorerQuery query, TeamsExplorerSources sources);

        Task<TeamsAdoption> GetAdoptionAsync(TeamsExplorerQuery query);

        Task<TeamsMeetings> GetMeetingsAsync(TeamsExplorerQuery query);

        Task<TeamsCollaboration> GetCollaborationAsync(TeamsExplorerQuery query);

        Task<TeamsConversations> GetConversationsAsync(TeamsExplorerQuery query, bool cognitiveAvailable);

        Task<TeamsPeople> GetPeopleAsync(TeamsExplorerQuery query);
    }

    /// <summary>
    /// Runs the Teams Explorer's SQL and turns the raw result sets into the page's models.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every section of a tab runs as its own command, on its own connection, in parallel, with its
    /// own short timeout. A failure is captured as a <see cref="TeamsExplorerQueryInfo.Error"/> rather
    /// than thrown, so one slow leaderboard degrades to a message beside nine working charts instead
    /// of taking the whole tab down. This is the same trade
    /// <c>ReportsAPIController</c> makes per chart, and it matters more here because several of these
    /// tables have no date index yet.
    /// </para>
    /// <para>
    /// Parameters are rebuilt for every command. A <see cref="SqlParameter"/> instance cannot be
    /// attached to two commands at once, so sharing one array across parallel queries would fail
    /// intermittently and only under load - exactly the bug that is hardest to find later.
    /// </para>
    /// <para>
    /// All judgement (segments, bands, concentration) is applied here from
    /// <see cref="TeamsExplorerScoring"/>, never in SQL, so the thresholds exist once and are unit
    /// tested.
    /// </para>
    /// </remarks>
    public sealed class SqlTeamsExplorerStore : ITeamsExplorerStore
    {
        private readonly IAnalyticsDbContextFactory _contextFactory;
        private readonly int _commandTimeoutSeconds;

        public SqlTeamsExplorerStore(IAnalyticsDbContextFactory contextFactory)
            : this(contextFactory, TeamsExplorerSql.CommandTimeoutSeconds)
        {
        }

        public SqlTeamsExplorerStore(IAnalyticsDbContextFactory contextFactory, int commandTimeoutSeconds)
        {
            _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
            _commandTimeoutSeconds = commandTimeoutSeconds;
        }

        #region Availability

        /// <summary>
        /// Total and authorised team counts. Returns null rather than zeros when the read fails: a
        /// failed query must not be reported to an admin as "no teams are authorised", which would
        /// send them to re-authorise teams that are already fine.
        /// </summary>
        public async Task<Tuple<int, int>> GetTeamCountsAsync()
        {
            try
            {
                using (var db = _contextFactory.Create())
                {
                    db.Database.CommandTimeout = _commandTimeoutSeconds;
                    var total = await db.Database
                        .SqlQuery<int>("SELECT COUNT(*) FROM dbo.teams").FirstOrDefaultAsync();
                    var authorised = await db.Database
                        .SqlQuery<int>("SELECT COUNT(*) FROM dbo.teams WHERE has_refresh_token = 1")
                        .FirstOrDefaultAsync();
                    return Tuple.Create(total, authorised);
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        #endregion

        #region Overview

        public async Task<TeamsOverview> GetOverviewAsync(TeamsExplorerQuery query, TeamsExplorerSources sources)
        {
            sources = sources ?? new TeamsExplorerSources();
            var window = TeamsExplorerWindow.From(query);
            var championDays = TeamsExplorerScoring.MinimumPowerDays(window.WorkingDays);

            var usageTask = RunAsync<OverviewUsageRow>("overview-usage", TeamsExplorerSql.OverviewUsage, query, championDays);
            var callsTask = RunAsync<CallCountRow>("overview-calls", TeamsExplorerSql.OverviewCalls(), query, championDays);
            var teamsTask = RunAsync<CollaborationKpiRow>("overview-teams", TeamsExplorerSql.CollaborationKpis, query, championDays);
            var trendTask = RunAsync<UsageTrendRow>("overview-trend", TeamsExplorerSql.OverviewTrend(), query, championDays);
            var callTrendTask = RunAsync<WeekCallCountRow>("overview-call-trend", TeamsExplorerSql.OverviewCallTrend(), query, championDays);
            var segmentTask = RunAsync<ActiveDayHistogramRow>("overview-segments", TeamsExplorerSql.SegmentHistogram, query, championDays);
            var organiserTask = RunAsync<OrganiserVolumeRow>("overview-organisers", TeamsExplorerSql.CallOrganiserVolumes, query, championDays);

            await Task.WhenAll(usageTask, callsTask, teamsTask, trendTask, callTrendTask, segmentTask, organiserTask)
                .ConfigureAwait(false);

            var usage = usageTask.Result;
            var calls = callsTask.Result;
            var teams = teamsTask.Result;

            var model = new TeamsOverview { Window = window };
            model.Queries.AddRange(new[]
            {
                usage.Info, calls.Info, teams.Info, trendTask.Result.Info,
                callTrendTask.Result.Info, segmentTask.Result.Info, organiserTask.Result.Info,
            });

            var usageRow = usage.Rows.FirstOrDefault() ?? new OverviewUsageRow();
            var callRow = calls.Rows.FirstOrDefault() ?? new CallCountRow();
            var teamRow = teams.Rows.FirstOrDefault() ?? new CollaborationKpiRow();

            var chatTotal = usageRow.ChannelMessages + usageRow.PrivateMessages;
            var audioHours = usageRow.AudioSeconds / 3600.0;

            model.Kpis = new TeamsOverviewKpis
            {
                KnownUsers = usageRow.KnownUsers,
                ActiveUsers = usageRow.ActiveUsers,
                ReachPct = TeamsExplorerScoring.Percentage(usageRow.ActiveUsers, usageRow.KnownUsers),
                ChannelMessages = usageRow.ChannelMessages,
                PrivateMessages = usageRow.PrivateMessages,
                OpenCollaborationPct = TeamsExplorerScoring.Percentage(usageRow.ChannelMessages, chatTotal),
                MeetingsAttended = usageRow.MeetingsAttended,
                MeetingsOrganised = usageRow.MeetingsOrganised,
                MeetingsPerActiveUser = usageRow.ActiveUsers > 0
                    ? (double)usageRow.MeetingsAttended / usageRow.ActiveUsers
                    : 0,
                AudioHours = audioHours,
                VideoSharePct = TeamsExplorerScoring.Percentage(usageRow.VideoSeconds, usageRow.AudioSeconds),
                ScreenShareSharePct = TeamsExplorerScoring.Percentage(usageRow.ScreenShareSeconds, usageRow.AudioSeconds),
                Calls = callRow.Calls,
                TotalTeams = teamRow.TotalTeams,
                ActiveTeams = teamRow.ActiveTeams,
                TotalChannels = teamRow.TotalChannels,
                ActiveChannels = teamRow.ActiveChannels,
            };

            model.Trend = MergeTrend(trendTask.Result.Rows, callTrendTask.Result.Rows);
            model.SegmentMix = BuildSegmentMix(segmentTask.Result.Rows, window.WorkingDays, usageRow.MeasuredUsers);

            model.Judgements = TeamsExplorerScoring.Judgements(new TeamsJudgementInputs
            {
                ActiveUsers = usageRow.ActiveUsers,
                KnownUsers = usageRow.KnownUsers,
                OpenCollaborationPct = model.Kpis.OpenCollaborationPct,
                MeetingsPerActiveUser = model.Kpis.MeetingsPerActiveUser,
                AfterHoursPct = TeamsExplorerScoring.Percentage(callRow.OutOfHoursCalls, callRow.Calls),
                OrganiserConcentrationPct =
                    TeamsExplorerScoring.TopDecileShare(organiserTask.Result.Rows.Select(r => r.Calls)),
                ActiveTeams = teamRow.ActiveTeams,
                TotalTeams = teamRow.TotalTeams,
                OwnerlessTeams = teamRow.OwnerlessTeams,
                UsageReportsAvailable = sources.UsageReports,
                CallsAvailable = sources.Calls,
                TeamsAnalyticsAvailable = sources.TeamsAnalytics,
            });

            return model;
        }

        /// <summary>
        /// Merges the usage-report trend and the call trend on their week buckets.
        /// </summary>
        /// <remarks>
        /// A full outer merge rather than a join on either side: the two series cover different
        /// windows (usage reports lag by a few days), so joining on one would silently drop the weeks
        /// that only the other has.
        /// </remarks>
        private static List<TeamsTrendPoint> MergeTrend(
            List<UsageTrendRow> usage,
            List<WeekCallCountRow> calls)
        {
            var byWeek = new SortedDictionary<DateTime, TeamsTrendPoint>();

            foreach (var row in usage)
            {
                byWeek[row.WeekStart] = new TeamsTrendPoint
                {
                    WeekStart = row.WeekStart,
                    ActiveUsers = row.ActiveUsers,
                    ChannelMessages = row.ChannelMessages,
                    PrivateMessages = row.PrivateMessages,
                    MeetingsAttended = row.MeetingsAttended,
                };
            }

            foreach (var row in calls)
            {
                if (!byWeek.TryGetValue(row.WeekStart, out var point))
                {
                    point = new TeamsTrendPoint { WeekStart = row.WeekStart };
                    byWeek[row.WeekStart] = point;
                }

                point.Calls = row.Calls;
            }

            return byWeek.Values.ToList();
        }

        /// <summary>Turns the raw active-day histogram into the four named segments.</summary>
        private static List<TeamsSegmentSlice> BuildSegmentMix(
            List<ActiveDayHistogramRow> histogram,
            int workingDays,
            int measuredUsers)
        {
            var counts = new Dictionary<TeamsUserSegment, int>
            {
                { TeamsUserSegment.Power, 0 },
                { TeamsUserSegment.Regular, 0 },
                { TeamsUserSegment.Light, 0 },
                { TeamsUserSegment.Dormant, 0 },
            };

            foreach (var row in histogram)
            {
                var segment = TeamsExplorerScoring.Segment(row.ActiveDays, workingDays);
                counts[segment] += (int)row.Users;
            }

            // The denominator is the users the usage report actually covered, not the whole directory:
            // someone with no licence is not a dormant Teams user, they are an unmeasured one.
            var total = measuredUsers > 0 ? measuredUsers : counts.Values.Sum();

            return counts
                .OrderByDescending(pair => (int)pair.Key)
                .Select(pair => new TeamsSegmentSlice
                {
                    Segment = pair.Key.ToString(),
                    Label = TeamsExplorerScoring.SegmentLabel(pair.Key),
                    Description = TeamsExplorerScoring.SegmentDescription(pair.Key),
                    Users = pair.Value,
                    SharePct = TeamsExplorerScoring.Percentage(pair.Value, total),
                })
                .ToList();
        }

        #endregion

        #region Adoption

        public async Task<TeamsAdoption> GetAdoptionAsync(TeamsExplorerQuery query)
        {
            var window = TeamsExplorerWindow.From(query);
            var championDays = TeamsExplorerScoring.MinimumPowerDays(window.WorkingDays);

            var rhythmTask = RunAsync<RhythmRow>("adoption-rhythm", TeamsExplorerSql.AdoptionRhythm(), query, championDays);
            var segmentTask = RunAsync<ActiveDayHistogramRow>("adoption-segments", TeamsExplorerSql.SegmentHistogram, query, championDays);
            var segmentTrendTask = RunAsync<WeeklyActiveDayHistogramRow>("adoption-segment-trend", TeamsExplorerSql.SegmentTrendHistogram(), query, championDays);
            var breakdownTask = RunAsync<BreakdownRow>("adoption-breakdown", TeamsExplorerSql.AdoptionBreakdown(query.GroupBy), query, championDays);
            var devicesTask = RunAsync<DevicesRow>("adoption-devices", TeamsExplorerSql.AdoptionDevices, query, championDays);
            var lifecycleTask = RunAsync<LifecycleRow>("adoption-lifecycle", TeamsExplorerSql.AdoptionLifecycle, query, championDays);
            var measuredTask = RunAsync<OverviewUsageRow>("adoption-usage", TeamsExplorerSql.OverviewUsage, query, championDays);

            await Task.WhenAll(rhythmTask, segmentTask, segmentTrendTask, breakdownTask, devicesTask, lifecycleTask, measuredTask)
                .ConfigureAwait(false);

            var model = new TeamsAdoption { Window = window, GroupBy = query.GroupBy };
            model.Queries.AddRange(new[]
            {
                rhythmTask.Result.Info, segmentTask.Result.Info, segmentTrendTask.Result.Info,
                breakdownTask.Result.Info, devicesTask.Result.Info, lifecycleTask.Result.Info,
                measuredTask.Result.Info,
            });

            var rhythm = rhythmTask.Result.Rows.FirstOrDefault() ?? new RhythmRow();
            model.Rhythm = new TeamsAdoptionRhythm
            {
                MeanDailyActiveUsers = rhythm.MeanDailyActiveUsers,
                WeeklyActiveUsers = rhythm.WeeklyActiveUsers,
                MonthlyActiveUsers = rhythm.MonthlyActiveUsers,
                StickinessPct = TeamsExplorerScoring.Percentage(rhythm.MeanDailyActiveUsers, rhythm.MonthlyActiveUsers),
            };

            var measured = measuredTask.Result.Rows.FirstOrDefault() ?? new OverviewUsageRow();
            model.SegmentMix = BuildSegmentMix(segmentTask.Result.Rows, window.WorkingDays, measured.MeasuredUsers);
            model.SegmentTrend = BuildSegmentTrend(segmentTrendTask.Result.Rows, query);

            model.Breakdown = breakdownTask.Result.Rows
                .Select(r => new TeamsDemographicRow
                {
                    Name = r.Name,
                    KnownUsers = r.KnownUsers,
                    ActiveUsers = r.ActiveUsers,
                    ReachPct = TeamsExplorerScoring.Percentage(r.ActiveUsers, r.KnownUsers),
                    MessagesPerActiveUser = r.ActiveUsers > 0 ? (double)r.Messages / r.ActiveUsers : 0,
                    MeetingsPerActiveUser = r.ActiveUsers > 0 ? (double)r.Meetings / r.ActiveUsers : 0,
                })
                .ToList();

            model.Devices = BuildDevices(devicesTask.Result.Rows.FirstOrDefault());

            var lifecycle = lifecycleTask.Result.Rows.FirstOrDefault() ?? new LifecycleRow();
            model.Lifecycle = new TeamsLifecycle
            {
                NewUsers = lifecycle.NewUsers,
                ReturningUsers = lifecycle.ReturningUsers,
                LapsedUsers = lifecycle.LapsedUsers,
            };

            return model;
        }

        /// <summary>
        /// Buckets each week's active-day histogram into segments.
        /// </summary>
        /// <remarks>
        /// The working-day denominator is computed PER WEEK rather than assumed to be five. The first
        /// and last weeks of a window are usually partial, and judging a user who was active on both
        /// available days of a two-day week against a five-day denominator would mis-file them as
        /// Light when they used Teams every day they could.
        /// </remarks>
        private static List<TeamsSegmentTrendPoint> BuildSegmentTrend(
            List<WeeklyActiveDayHistogramRow> rows,
            TeamsExplorerQuery query)
        {
            var byWeek = new SortedDictionary<DateTime, TeamsSegmentTrendPoint>();

            foreach (var row in rows)
            {
                if (!byWeek.TryGetValue(row.WeekStart, out var point))
                {
                    point = new TeamsSegmentTrendPoint { WeekStart = row.WeekStart };
                    byWeek[row.WeekStart] = point;
                }

                var weekStart = row.WeekStart < query.UsageFromUtc ? query.UsageFromUtc : row.WeekStart;
                var weekEnd = row.WeekStart.AddDays(7);
                if (weekEnd > query.UsageToExclusiveUtc) weekEnd = query.UsageToExclusiveUtc;

                var workingDays = TeamsExplorerScoring.WorkingDaysBetween(weekStart, weekEnd);
                var users = (int)row.Users;

                switch (TeamsExplorerScoring.Segment(row.ActiveDays, workingDays))
                {
                    case TeamsUserSegment.Power:
                        point.Power += users;
                        break;
                    case TeamsUserSegment.Regular:
                        point.Regular += users;
                        break;
                    default:
                        point.Light += users;
                        break;
                }
            }

            return byWeek.Values.ToList();
        }

        private static List<TeamsDeviceRow> BuildDevices(DevicesRow row)
        {
            row = row ?? new DevicesRow();

            var platforms = new[]
            {
                new { Platform = "Windows", Users = row.UsedWindows },
                new { Platform = "Mac", Users = row.UsedMac },
                new { Platform = "Web", Users = row.UsedWeb },
                new { Platform = "iOS", Users = row.UsedIos },
                new { Platform = "Android", Users = row.UsedAndroid },
                new { Platform = "Linux", Users = row.UsedLinux },
                new { Platform = "Chrome OS", Users = row.UsedChromeOs },
                new { Platform = "Mobile (any)", Users = row.UsedMobile },
            };

            return platforms
                .Where(p => p.Users > 0)
                .OrderByDescending(p => p.Users)
                .Select(p => new TeamsDeviceRow
                {
                    Platform = p.Platform,
                    Users = p.Users,
                    SharePct = TeamsExplorerScoring.Percentage(p.Users, row.MeasuredUsers),
                })
                .ToList();
        }

        #endregion

        #region Meetings and calls

        public async Task<TeamsMeetings> GetMeetingsAsync(TeamsExplorerQuery query)
        {
            var window = TeamsExplorerWindow.From(query);
            var championDays = TeamsExplorerScoring.MinimumPowerDays(window.WorkingDays);

            var kpiTask = RunAsync<CallKpiRow>("calls-kpis", TeamsExplorerSql.CallKpis(), query, championDays);
            var organiserVolumeTask = RunAsync<OrganiserVolumeRow>("calls-organiser-volumes", TeamsExplorerSql.CallOrganiserVolumes, query, championDays);
            var trendTask = RunAsync<CallTrendRow>("calls-trend", TeamsExplorerSql.CallTrend(), query, championDays);
            var sizeTask = RunAsync<CallSizeRow>("calls-sizes", TeamsExplorerSql.CallSizes, query, championDays);
            var durationTask = RunAsync<CallDurationRow>("calls-durations", TeamsExplorerSql.CallDurations, query, championDays);
            var heatTask = RunAsync<HeatRow>("calls-heatmap", TeamsExplorerSql.CallHeatmap(), query, championDays);
            var modalityTask = RunAsync<NameCountRow>("calls-modalities", TeamsExplorerSql.CallModalities, query, championDays);
            var organiserTask = RunAsync<NameCountRow>("calls-top-organisers", TeamsExplorerSql.TopOrganisers, query, championDays);
            var attendeeTask = RunAsync<NameCountRow>("calls-top-attendees", TeamsExplorerSql.TopAttendees, query, championDays);
            var ratingTask = RunAsync<FeedbackRatingRow>("calls-ratings", TeamsExplorerSql.CallFeedbackRatings, query, championDays);
            var failureTask = RunAsync<FailureRow>("calls-failures", TeamsExplorerSql.CallFailures, query, championDays);

            await Task.WhenAll(
                    kpiTask, organiserVolumeTask, trendTask, sizeTask, durationTask, heatTask,
                    modalityTask, organiserTask, attendeeTask, ratingTask, failureTask)
                .ConfigureAwait(false);

            var model = new TeamsMeetings
            {
                Window = window,
                WorkingDayStartHour = TeamsExplorerScoring.WorkingDayStartHour,
                WorkingDayEndHour = TeamsExplorerScoring.WorkingDayEndHour,
            };

            model.Queries.AddRange(new[]
            {
                kpiTask.Result.Info, organiserVolumeTask.Result.Info, trendTask.Result.Info,
                sizeTask.Result.Info, durationTask.Result.Info, heatTask.Result.Info,
                modalityTask.Result.Info, organiserTask.Result.Info, attendeeTask.Result.Info,
                ratingTask.Result.Info, failureTask.Result.Info,
            });

            var kpi = kpiTask.Result.Rows.FirstOrDefault() ?? new CallKpiRow();

            model.Kpis = new TeamsCallKpis
            {
                Calls = kpi.Calls,
                GroupCalls = kpi.GroupCalls,
                PeerToPeerCalls = kpi.PeerToPeerCalls,
                Attendees = kpi.DistinctAttendees,
                CallHours = kpi.CallSeconds / 3600.0,
                AttendeeHours = kpi.AttendeeSeconds / 3600.0,
                MeanDurationMinutes = kpi.Calls > 0 ? kpi.CallSeconds / 60.0 / kpi.Calls : 0,
                MeanAttendees = kpi.MeanAttendees,
                AfterHoursPct = TeamsExplorerScoring.Percentage(kpi.OutOfHoursCalls, kpi.Calls),
                WeekendPct = TeamsExplorerScoring.Percentage(kpi.WeekendCalls, kpi.Calls),
                OrganiserConcentrationPct =
                    TeamsExplorerScoring.TopDecileShare(organiserVolumeTask.Result.Rows.Select(r => r.Calls)),
                AttendeeEngagementPct = (kpi.AttendeeEngagement ?? 0) * 100.0,
            };

            model.Trend = trendTask.Result.Rows
                .Select(r => new TeamsCallTrendPoint
                {
                    WeekStart = r.WeekStart,
                    Calls = r.Calls,
                    Minutes = r.Minutes,
                    Attendees = r.Attendees,
                })
                .ToList();

            model.SizeDistribution = BuildSizeBuckets(sizeTask.Result.Rows);
            model.DurationDistribution = BuildDurationBuckets(durationTask.Result.Rows);

            model.Heatmap = heatTask.Result.Rows
                .Select(r => new TeamsHeatCell { DayOfWeek = r.DayOfWeek, Hour = r.Hour, Calls = r.Calls })
                .ToList();

            model.PeriodOfDay = BuildPeriodOfDay(heatTask.Result.Rows);
            model.ModalityMix = ToBuckets(modalityTask.Result.Rows);
            model.TopOrganisers = ToNamedRows(organiserTask.Result.Rows);
            model.TopAttendees = ToNamedRows(attendeeTask.Result.Rows);

            model.Quality = BuildQuality(ratingTask.Result.Rows, failureTask.Result.Rows);

            return model;
        }

        /// <summary>Meeting-size buckets. The boundaries live here rather than in SQL so they are testable.</summary>
        private static List<TeamsBucketRow> BuildSizeBuckets(List<CallSizeRow> rows)
        {
            var buckets = new[]
            {
                new { Key = "1", Label = "1 (no one joined)", Max = 1L },
                new { Key = "2", Label = "2", Max = 2L },
                new { Key = "3-5", Label = "3-5", Max = 5L },
                new { Key = "6-10", Label = "6-10", Max = 10L },
                new { Key = "11-25", Label = "11-25", Max = 25L },
                new { Key = "26-50", Label = "26-50", Max = 50L },
                new { Key = "50+", Label = "More than 50", Max = long.MaxValue },
            };

            var counts = buckets.ToDictionary(b => b.Key, b => 0L);

            foreach (var row in rows)
            {
                var bucket = buckets.First(b => row.Attendees <= b.Max);
                counts[bucket.Key] += row.Calls;
            }

            var total = counts.Values.Sum();

            return buckets
                .Select(b => new TeamsBucketRow
                {
                    Key = b.Key,
                    Label = b.Label,
                    Count = counts[b.Key],
                    SharePct = TeamsExplorerScoring.Percentage(counts[b.Key], total),
                })
                .ToList();
        }

        /// <summary>Call-duration buckets, in minutes.</summary>
        private static List<TeamsBucketRow> BuildDurationBuckets(List<CallDurationRow> rows)
        {
            var buckets = new[]
            {
                new { Key = "<5", Label = "Under 5 minutes", Max = 4 },
                new { Key = "5-15", Label = "5-15 minutes", Max = 15 },
                new { Key = "15-30", Label = "15-30 minutes", Max = 30 },
                new { Key = "30-60", Label = "30-60 minutes", Max = 60 },
                new { Key = "60-120", Label = "1-2 hours", Max = 120 },
                new { Key = "120+", Label = "More than 2 hours", Max = int.MaxValue },
            };

            var counts = buckets.ToDictionary(b => b.Key, b => 0L);

            foreach (var row in rows)
            {
                var bucket = buckets.First(b => row.Minutes <= b.Max);
                counts[bucket.Key] += row.Calls;
            }

            var total = counts.Values.Sum();

            return buckets
                .Select(b => new TeamsBucketRow
                {
                    Key = b.Key,
                    Label = b.Label,
                    Count = counts[b.Key],
                    SharePct = TeamsExplorerScoring.Percentage(counts[b.Key], total),
                })
                .ToList();
        }

        /// <summary>
        /// The archived Power BI report's "period of day", rebuilt from the heatmap rather than from a
        /// second query - the hour buckets already contain the answer.
        /// </summary>
        private static List<TeamsBucketRow> BuildPeriodOfDay(List<HeatRow> rows)
        {
            var buckets = new[]
            {
                new { Key = "early", Label = "Early morning (before 09:00)", Max = 8 },
                new { Key = "late-morning", Label = "Late morning (09:00-12:00)", Max = 11 },
                new { Key = "afternoon", Label = "Afternoon (12:00-17:00)", Max = 16 },
                new { Key = "evening", Label = "Evening (17:00-21:00)", Max = 20 },
                new { Key = "night", Label = "Night (after 21:00)", Max = 23 },
            };

            var counts = buckets.ToDictionary(b => b.Key, b => 0L);

            foreach (var row in rows)
            {
                var bucket = buckets.First(b => row.Hour <= b.Max);
                counts[bucket.Key] += row.Calls;
            }

            var total = counts.Values.Sum();

            return buckets
                .Select(b => new TeamsBucketRow
                {
                    Key = b.Key,
                    Label = b.Label,
                    Count = counts[b.Key],
                    SharePct = TeamsExplorerScoring.Percentage(counts[b.Key], total),
                })
                .ToList();
        }

        private static TeamsCallQuality BuildQuality(List<FeedbackRatingRow> ratings, List<FailureRow> failures)
        {
            var quality = new TeamsCallQuality
            {
                FeedbackCount = ratings.Sum(r => r.Feedback),
                FailureCount = failures.Sum(f => f.Failures),
            };

            quality.Ratings = ratings
                .Select(r => new TeamsBucketRow
                {
                    Key = r.Rating,
                    Label = r.Rating,
                    Count = r.Feedback,
                    SharePct = TeamsExplorerScoring.Percentage(r.Feedback, quality.FeedbackCount),
                })
                .ToList();

            quality.FailureReasons = failures
                .GroupBy(f => f.Reason)
                .Select(g => new TeamsBucketRow
                {
                    Key = g.Key,
                    Label = g.Key,
                    Count = g.Sum(f => f.Failures),
                    SharePct = TeamsExplorerScoring.Percentage(g.Sum(f => f.Failures), quality.FailureCount),
                })
                .OrderByDescending(b => b.Count)
                .ToList();

            quality.FailureStages = failures
                .GroupBy(f => f.Stage)
                .Select(g => new TeamsBucketRow
                {
                    Key = g.Key,
                    Label = g.Key,
                    Count = g.Sum(f => f.Failures),
                    SharePct = TeamsExplorerScoring.Percentage(g.Sum(f => f.Failures), quality.FailureCount),
                })
                .OrderByDescending(b => b.Count)
                .ToList();

            return quality;
        }

        #endregion

        #region Collaboration

        public async Task<TeamsCollaboration> GetCollaborationAsync(TeamsExplorerQuery query)
        {
            var window = TeamsExplorerWindow.From(query);
            var championDays = TeamsExplorerScoring.MinimumPowerDays(window.WorkingDays);

            var kpiTask = RunAsync<CollaborationKpiRow>("collab-kpis", TeamsExplorerSql.CollaborationKpis, query, championDays);
            var teamTask = RunAsync<TeamLeaderboardRow>("collab-teams", TeamsExplorerSql.TeamLeaderboard, query, championDays);
            var channelTask = RunAsync<ChannelLeaderboardRow>("collab-channels", TeamsExplorerSql.ChannelLeaderboard, query, championDays);
            var ownerlessTask = RunAsync<NameCountRow>("collab-ownerless", TeamsExplorerSql.OwnerlessTeams, query, championDays);
            var dormantTask = RunAsync<NameCountRow>("collab-dormant", TeamsExplorerSql.DormantTeams, query, championDays);
            var reactionTask = RunAsync<NameCountRow>("collab-reactions", TeamsExplorerSql.ReactionMix, query, championDays);
            var tabTask = RunAsync<NameCountRow>("collab-tabs", TeamsExplorerSql.TabUsage, query, championDays);
            var membershipTask = RunAsync<MembershipTrendRow>("collab-membership", TeamsExplorerSql.MembershipTrend(), query, championDays);

            await Task.WhenAll(kpiTask, teamTask, channelTask, ownerlessTask, dormantTask, reactionTask, tabTask, membershipTask)
                .ConfigureAwait(false);

            var model = new TeamsCollaboration { Window = window };
            model.Queries.AddRange(new[]
            {
                kpiTask.Result.Info, teamTask.Result.Info, channelTask.Result.Info,
                ownerlessTask.Result.Info, dormantTask.Result.Info, reactionTask.Result.Info,
                tabTask.Result.Info, membershipTask.Result.Info,
            });

            var kpi = kpiTask.Result.Rows.FirstOrDefault() ?? new CollaborationKpiRow();
            model.Kpis = new TeamsCollaborationKpis
            {
                TotalTeams = kpi.TotalTeams,
                ActiveTeams = kpi.ActiveTeams,
                DormantTeams = Math.Max(0, kpi.AuthorisedTeams - kpi.ActiveTeams),
                OwnerlessTeams = kpi.OwnerlessTeams,
                AuthorisedTeams = kpi.AuthorisedTeams,
                TotalChannels = kpi.TotalChannels,
                ActiveChannels = kpi.ActiveChannels,
                ChannelMessages = kpi.ChannelMessages,
                Reactions = kpi.Reactions,
            };

            model.Teams = teamTask.Result.Rows
                .Select(r => new TeamsTeamRow
                {
                    Id = r.Id,
                    Name = r.Name,
                    Members = r.Members,
                    Owners = r.Owners,
                    Channels = r.Channels,
                    Tabs = r.Tabs,
                    Messages = r.Messages,
                    Reactions = r.Reactions,
                    Sentiment = r.Sentiment,
                    ActiveDays = r.ActiveDays,
                    Authorised = r.Authorised == 1,
                })
                .ToList();

            model.Channels = channelTask.Result.Rows
                .Select(r => new TeamsChannelRow
                {
                    Id = r.Id,
                    Name = r.Name,
                    TeamName = r.TeamName,
                    Tabs = r.Tabs,
                    Messages = r.Messages,
                    Reactions = r.Reactions,
                    ReactingUsers = r.ReactingUsers,
                    Sentiment = r.Sentiment,
                    ActiveDays = r.ActiveDays,
                })
                .ToList();

            model.OwnerlessTeams = ToNamedRows(ownerlessTask.Result.Rows);
            model.DormantTeams = ToNamedRows(dormantTask.Result.Rows);
            model.ReactionMix = ToBuckets(reactionTask.Result.Rows);
            model.TabUsage = ToNamedRows(tabTask.Result.Rows);

            model.MembershipTrend = membershipTask.Result.Rows
                .Select(r => new TeamsTrendPoint { WeekStart = r.WeekStart, ActiveUsers = r.ActiveUsers })
                .ToList();

            return model;
        }

        #endregion

        #region Conversations

        public async Task<TeamsConversations> GetConversationsAsync(TeamsExplorerQuery query, bool cognitiveAvailable)
        {
            var window = TeamsExplorerWindow.From(query);
            var championDays = TeamsExplorerScoring.MinimumPowerDays(window.WorkingDays);

            var keywordTask = RunAsync<NameCountRow>("conv-keywords", TeamsExplorerSql.Keywords, query, championDays);
            var languageTask = RunAsync<NameCountRow>("conv-languages", TeamsExplorerSql.Languages, query, championDays);
            var trendTask = RunAsync<SentimentTrendRow>("conv-sentiment-trend", TeamsExplorerSql.SentimentTrend(), query, championDays);
            var teamTask = RunAsync<SentimentRow>("conv-sentiment-team", TeamsExplorerSql.SentimentByTeam, query, championDays);
            var channelTask = RunAsync<SentimentRow>("conv-sentiment-channel", TeamsExplorerSql.SentimentByChannel, query, championDays);
            var scoredTask = RunAsync<ScoredChannelDaysRow>("conv-scored-days", TeamsExplorerSql.ScoredChannelDays, query, championDays);

            await Task.WhenAll(keywordTask, languageTask, trendTask, teamTask, channelTask, scoredTask)
                .ConfigureAwait(false);

            var model = new TeamsConversations
            {
                Window = window,
                CognitiveAvailable = cognitiveAvailable,
                ScoredChannelDays = scoredTask.Result.Rows.FirstOrDefault()?.ScoredChannelDays ?? 0,
            };

            model.Queries.AddRange(new[]
            {
                keywordTask.Result.Info, languageTask.Result.Info, trendTask.Result.Info,
                teamTask.Result.Info, channelTask.Result.Info, scoredTask.Result.Info,
            });

            model.Keywords = ToNamedRows(keywordTask.Result.Rows);
            model.Languages = ToNamedRows(languageTask.Result.Rows);

            model.SentimentTrend = trendTask.Result.Rows
                .Select(r => new TeamsSentimentPoint
                {
                    WeekStart = r.WeekStart,
                    Sentiment = r.Sentiment,
                    Messages = r.Messages,
                })
                .ToList();

            model.SentimentByTeam = ToSentimentRows(teamTask.Result.Rows);
            model.SentimentByChannel = ToSentimentRows(channelTask.Result.Rows);

            return model;
        }

        #endregion

        #region People

        public async Task<TeamsPeople> GetPeopleAsync(TeamsExplorerQuery query)
        {
            var window = TeamsExplorerWindow.From(query);
            var championDays = TeamsExplorerScoring.MinimumPowerDays(window.WorkingDays);

            var championTask = RunAsync<PersonRow>("people-champions", TeamsExplorerSql.Champions, query, championDays);
            var dormantTask = RunAsync<PersonRow>("people-dormant", TeamsExplorerSql.DormantUsers, query, championDays);
            var departmentTask = RunAsync<NameCountRow>("people-departments", TeamsExplorerSql.ChampionsByDepartment, query, championDays);
            var obfuscationTask = RunAsync<ObfuscationProbeRow>("people-obfuscation", TeamsExplorerSql.ObfuscationProbe, query, championDays);

            await Task.WhenAll(championTask, dormantTask, departmentTask, obfuscationTask).ConfigureAwait(false);

            var model = new TeamsPeople { Window = window };
            model.Queries.AddRange(new[]
            {
                championTask.Result.Info, dormantTask.Result.Info,
                departmentTask.Result.Info, obfuscationTask.Result.Info,
            });

            model.Champions = championTask.Result.Rows
                .Select(r => ToPerson(r, window.WorkingDays))
                .ToList();

            model.Dormant = dormantTask.Result.Rows
                .Select(r => ToPerson(r, window.WorkingDays))
                .ToList();

            model.ChampionsByDepartment = ToNamedRows(departmentTask.Result.Rows);

            var probe = obfuscationTask.Result.Rows.FirstOrDefault();
            // Only conclude obfuscation from a real sample. A tenant with no reported users at all
            // would otherwise be told its usage reports are anonymised, which is a different problem
            // with a different fix.
            model.NamesObfuscated = probe != null && probe.Sampled > 0 && probe.WithAddress == 0;

            return model;
        }

        private static TeamsPersonRow ToPerson(PersonRow row, int workingDays)
        {
            return new TeamsPersonRow
            {
                UserPrincipalName = row.UserPrincipalName,
                Department = row.Department,
                ActiveDays = row.ActiveDays,
                ChannelMessages = row.ChannelMessages,
                PrivateMessages = row.PrivateMessages,
                MeetingsOrganised = row.MeetingsOrganised,
                MeetingsAttended = row.MeetingsAttended,
                CallsHosted = row.CallsHosted,
                CallsAttended = row.CallsAttended,
                Segment = TeamsExplorerScoring.SegmentLabel(
                    TeamsExplorerScoring.Segment(row.ActiveDays, workingDays)),
                LastActivity = row.LastActivity,
            };
        }

        #endregion

        #region Execution

        private sealed class QueryResult<T>
        {
            public List<T> Rows { get; set; } = new List<T>();
            public TeamsExplorerQueryInfo Info { get; set; }
        }

        /// <summary>
        /// Runs one statement, timing it and converting any failure into a per-section error.
        /// </summary>
        private async Task<QueryResult<T>> RunAsync<T>(
            string key,
            string sql,
            TeamsExplorerQuery query,
            int championDays)
        {
            var info = new TeamsExplorerQueryInfo
            {
                Key = key,
                Sql = TeamsExplorerSql.Describe(sql, query, championDays),
            };

            var result = new QueryResult<T> { Info = info };
            var watch = Stopwatch.StartNew();

            try
            {
                using (var db = _contextFactory.Create())
                {
                    db.Database.CommandTimeout = _commandTimeoutSeconds;
                    result.Rows = await db.Database
                        .SqlQuery<T>(sql, Parameters(query, championDays))
                        .ToListAsync()
                        .ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                info.Error = InnermostMessage(ex);
            }
            finally
            {
                watch.Stop();
                info.ElapsedMs = watch.ElapsedMilliseconds;
            }

            return result;
        }

        /// <summary>
        /// The one parameter list every statement is executed with.
        /// </summary>
        /// <remarks>
        /// SQL Server accepts parameters that a statement does not reference, so a single list keeps
        /// the executed command and the SQL shown in the UI's popover in step by construction. New
        /// instances every call: a <see cref="SqlParameter"/> belongs to one command at a time, and
        /// these statements run in parallel.
        /// </remarks>
        private static SqlParameter[] Parameters(TeamsExplorerQuery query, int championDays)
        {
            return new[]
            {
                DateParameter("from", query.FromUtc),
                DateParameter("to", query.ToExclusiveUtc),
                DateParameter("usageFrom", query.UsageFromUtc),
                DateParameter("usageTo", query.UsageToExclusiveUtc),
                DateParameter("wauFrom", TeamsExplorerSql.WeeklyActiveFrom(query)),
                DateParameter("mauFrom", TeamsExplorerSql.MonthlyActiveFrom(query)),
                DateParameter("midpoint", TeamsExplorerSql.Midpoint(query)),
                IntParameter("top", query.Top),
                IntParameter("workStart", TeamsExplorerScoring.WorkingDayStartHour),
                IntParameter("workEnd", TeamsExplorerScoring.WorkingDayEndHour),
                IntParameter("championDays", championDays),
            };
        }

        /// <summary>
        /// A date-only parameter. These tables store dates at midnight, so comparing against a
        /// <c>date</c> keeps the predicate an index seek and matches the SQL the popover shows.
        /// </summary>
        private static SqlParameter DateParameter(string name, DateTime value)
        {
            return new SqlParameter(name, SqlDbType.Date) { Value = value.Date };
        }

        private static SqlParameter IntParameter(string name, int value)
        {
            return new SqlParameter(name, SqlDbType.Int) { Value = value };
        }

        /// <summary>EF wraps SQL errors; the innermost message (the SqlException) is the useful one.</summary>
        private static string InnermostMessage(Exception ex)
        {
            var current = ex;
            while (current.InnerException != null)
            {
                current = current.InnerException;
            }

            return current.Message;
        }

        #endregion

        #region Shared projections

        private static List<TeamsNamedCountRow> ToNamedRows(List<NameCountRow> rows)
        {
            var total = rows.Sum(r => r.Count);

            return rows
                .Select(r => new TeamsNamedCountRow
                {
                    Name = r.Name,
                    Count = r.Count,
                    SharePct = total > 0 ? TeamsExplorerScoring.Percentage(r.Count, total) : (double?)null,
                })
                .ToList();
        }

        private static List<TeamsBucketRow> ToBuckets(List<NameCountRow> rows)
        {
            var total = rows.Sum(r => r.Count);

            return rows
                .Select(r => new TeamsBucketRow
                {
                    Key = r.Name,
                    Label = r.Name,
                    Count = r.Count,
                    SharePct = TeamsExplorerScoring.Percentage(r.Count, total),
                })
                .ToList();
        }

        private static List<TeamsSentimentRow> ToSentimentRows(List<SentimentRow> rows)
        {
            return rows
                .Select(r => new TeamsSentimentRow
                {
                    Name = r.Name,
                    Sentiment = r.Sentiment,
                    Messages = r.Messages,
                })
                .ToList();
        }

        #endregion
    }
}
