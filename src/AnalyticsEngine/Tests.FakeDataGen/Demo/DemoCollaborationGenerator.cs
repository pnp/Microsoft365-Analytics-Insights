using System;
using System.Collections.Generic;
using Tests.FakeDataGen.Seeding;

namespace Tests.FakeDataGen.Demo
{
    /// <summary>
    /// Representative detail follows the common timeline. Channel/group summaries retain only
    /// O(departments * days) counters; call partners use a bounded sample, not all users' timelines.
    /// </summary>
    internal sealed class DemoCollaborationGenerator
    {
        private static readonly string[] KeywordNames =
            { "Contoso planning", "Contoso customer success", "Contoso onboarding", "Contoso research", "Συνεργασία" };
        private static readonly string[] LanguageNames = { "English", "French", "Greek" };
        private static readonly string[] Subjects =
        {
            "Contoso project update", "Contoso customer workshop follow-up", "Contoso budget review",
            "Contoso onboarding next steps", "Contoso research – Καλημέρα κόσμε"
        };
        private static readonly string[] CommentTexts =
        {
            "Thanks, the Contoso guidance resolved my question.",
            "Can we clarify the next steps for the Contoso project?",
            "The Contoso instructions need another example.",
            "Καλημέρα κόσμε – ευχαριστούμε για τη συνεργασία!"
        };
        private static readonly string[] Ratings = { "good", "poor", "fair", "excellent" };
        private readonly DemoOptions _options;
        private readonly DemoPopulation _population;
        private readonly DemoCalendar _calendar;
        private readonly IDemoSink _sink;
        private readonly int[,] _channelMessages, _groupReads, _groupPosts, _groupLikes;
        private readonly int[] _members;
        private readonly List<int>[] _callPartners;
        private int _calls, _sessions, _emails, _comments, _lastUser, _likedPages;
        private bool _dimensionsWritten, _summariesWritten;

        public DemoCollaborationGenerator(DemoOptions options, DemoPopulation population, DemoCalendar calendar, IDemoSink sink)
        {
            _options = options;
            _population = population;
            _calendar = calendar;
            _sink = sink;
            int departments = SeedDataCatalogue.Departments.Length;
            _members = new int[departments];
            if (options.Includes(DemoArea.Teams))
            {
                _channelMessages = new int[departments * 2, options.Days];
                _callPartners = new List<int>[options.Days];
                for (int day = 0; day < options.Days; day++) _callPartners[day] = new List<int>();
            }
            if (options.Includes(DemoArea.Engage))
            {
                _groupReads = new int[departments, options.Days];
                _groupPosts = new int[departments, options.Days];
                _groupLikes = new int[departments, options.Days];
            }
        }

        public void WriteDimensions()
        {
            if (_dimensionsWritten) throw new InvalidOperationException("Collaboration dimensions already written.");
            _dimensionsWritten = true;
            if (_options.Includes(DemoArea.Teams)) WriteTeamDimensions();
            if (_options.Includes(DemoArea.Teams | DemoArea.Web | DemoArea.CopilotHistory))
                WriteNames(DemoTables.Languages, LanguageNames);
            if (_options.Includes(DemoArea.Teams | DemoArea.CopilotHistory)) WriteNames(DemoTables.Keywords, KeywordNames);
            // Page metadata/comments/likes arrive as AppInsights custom events under WebTraffic,
            // not through the SharePoint management audit or Graph usage-report import.
            if (_options.Includes(DemoArea.Web)) WritePageDimensions();
            if (_options.Includes(DemoArea.Web))
            {
                WriteNames(DemoTables.ClickTitles, new[] { "Contoso guidance", "Contoso project workspace", "Contoso learning" });
                WriteNames(DemoTables.ClickClasses, new[] { "contoso-navigation-link", "contoso-quick-link", "contoso-learning-card" });
                WriteNames(DemoTables.SearchTerms, new[]
                {
                    "Contoso travel policy", "Contoso onboarding", "Contoso project planning",
                    "Contoso customer workshop", "Καλημέρα κόσμε"
                });
            }
            if (_options.Includes(DemoArea.Engage))
                for (int department = 0; department < _members.Length; department++)
                    _sink.Write(DemoTables.EngageGroups, department + 1,
                        "Contoso " + SeedDataCatalogue.Departments[department] + " Community");

            if (_options.Includes(DemoArea.Teams | DemoArea.SentEmail | DemoArea.Engage))
            {
                var owners = new bool[_members.Length];
                for (int id = 1; id <= _options.Users; id++)
                {
                    var user = _population.User(id);
                    _members[user.Department]++;
                    if (_options.Includes(DemoArea.SentEmail))
                        _sink.Write(DemoTables.EmailAddresses, id, user.Upn);
                    if (!_options.Includes(DemoArea.Teams)) continue;
                    int team = user.Department + 1;
                    _sink.Write(DemoTables.TeamMemberships, id, _options.Start, team);
                    _sink.Write(DemoTables.TeamMemberships, id, _options.ReportEnd, team);
                    if (!owners[user.Department])
                    {
                        _sink.Write(DemoTables.TeamOwners, id, _options.Start, team);
                        owners[user.Department] = true;
                    }
                }
            }
            if (_options.Includes(DemoArea.SentEmail))
            {
                _sink.Write(DemoTables.EmailAddresses, _options.Users + 1, "contoso.workshops@contoso.example");
                _sink.Write(DemoTables.EmailAddresses, _options.Users + 2, "contoso.partners@contoso.example");
            }
            if (_options.Includes(DemoArea.Teams)) PrepareCallPartners();
        }

        private void WriteTeamDimensions()
        {
            WriteNames(DemoTables.CallTypes, new[] { "peerToPeer", "groupCall" });
            WriteNames(DemoTables.CallModalities, new[] { "audio", "video", "screenSharing" });
            WriteNames(DemoTables.ReactionTypes, new[] { "like", "heart", "laugh", "surprised" });
            for (int department = 0; department < _members.Length; department++)
            {
                int team = department + 1;
                _sink.Write(DemoTables.TeamDefinitions, team, _options.Start, false, null,
                    DemoRandom.Id(_options.Seed, 1000, team).ToString(),
                    "Contoso " + SeedDataCatalogue.Departments[department]);
                for (int channel = 0; channel < 2; channel++)
                {
                    int id = department * 2 + channel + 1;
                    _sink.Write(DemoTables.TeamChannels, id, DemoRandom.Id(_options.Seed, 1001, id).ToString(),
                        channel == 0 ? "Contoso General" : "Contoso Projects", team);
                    _sink.Write(DemoTables.TeamTabs, id,
                        "https://contoso.sharepoint.com/sites/demo-" + team.ToString("D2")
                            + "/SitePages/Contoso-demo-page-" + (channel + 1) + ".aspx",
                        DemoRandom.Id(_options.Seed, 1002, id).ToString(),
                        channel == 0 ? "Contoso Knowledge" : "Contoso Delivery Plan");
                    _sink.Write(DemoTables.TeamTabLogs, id, _options.Start, id);
                    _sink.Write(DemoTables.TeamTabLogs, id, _options.ReportEnd, id);
                }
            }
        }

        private void WritePageDimensions()
        {
            WriteNames(DemoTables.PageFields, new[] { "Title", "ContosoDepartment", "ContosoTopic", "ContentType" });
            for (int department = 0; department < _members.Length; department++)
                for (int page = 0; page < 3; page++)
                {
                    int url = department * 3 + page + 1;
                    _sink.Write(DemoTables.PageMetadata, url, 1,
                        page == 2 ? "Καλημέρα κόσμε" : page == 0 ? "Welcome" : "Working together", null, _options.Start);
                    _sink.Write(DemoTables.PageMetadata, url, 2, SeedDataCatalogue.Departments[department], null, _options.Start);
                    _sink.Write(DemoTables.PageMetadata, url, 3, KeywordNames[(department + page) % KeywordNames.Length],
                        DemoRandom.Id(_options.Seed, 1003, (department + page) % KeywordNames.Length + 1), _options.Start);
                    _sink.Write(DemoTables.PageMetadata, url, 4, "Site Page", null, _options.Start);
                }
        }

        private void PrepareCallPartners()
        {
            // Avoid either O(users * days) retained timelines or a fresh full timeline per call.
            int count = Math.Min(64, _options.Users);
            for (int sample = 0; sample < count; sample++)
            {
                int id = 1 + (int)((long)sample * _options.Users / count);
                var user = _population.User(id);
                if (user.Cohort == DemoCohort.Zero) continue;
                var timeline = new DemoTimeline(_options, user);
                for (int day = 0; day < _options.Days; day++)
                    if (timeline.Day(day).Meetings > 0) _callPartners[day].Add(id);
            }
        }

        public void WriteDay(DemoUser user, int day, DemoDay activity)
        {
            if (!_dimensionsWritten || _summariesWritten) throw new InvalidOperationException("Invalid collaboration generation order.");
            if (user.Cohort == DemoCohort.Zero || !activity.HasWorkloadActivity) return;
            if (!DemoCalendar.IsWorkingDate(_options.Start.AddDays(day)) || DemoTimeline.IsOnLeave(user.Id, day)
                || (user.Cohort == DemoCohort.Inactive && _options.Days - day <= 60)) return;
            if (_lastUser != user.Id) { _lastUser = user.Id; _likedPages = 0; }
            if (_options.Includes(DemoArea.Teams))
            {
                if (activity.Meetings > 0) WriteCall(user, day);
                if (activity.Messages > 0) WriteChannelActivity(user, day, activity);
            }
            if (_options.Includes(DemoArea.SentEmail) && activity.Sent > 0) WriteEmail(user, day, activity);
            if (_options.Includes(DemoArea.Web) && activity.SharePointFiles > 0) WritePageActivity(user, day, activity);
            if (_options.Includes(DemoArea.Web) && activity.SharePointFiles > 0) WriteWebActivity(user, day, activity);
            var date = _options.Start.AddDays(day);
            if (date > _options.ReportEnd) return;
            if (_options.Includes(DemoArea.OneDrive) && activity.OneDriveFiles > 0)
            {
                long files = 120 + user.Id % 500 + day * 2;
                _sink.Write(DemoTables.OneDriveStorage, user.Id, date, date,
                    files * (250000L + user.Id % 100 * 10000L), (long)activity.OneDriveFiles, files);
            }
            if (_options.Includes(DemoArea.Engage) && activity.EngageRead > 0)
            {
                _groupReads[user.Department, day] += activity.EngageRead;
                _groupPosts[user.Department, day] += activity.EngageRead / 3;
                _groupLikes[user.Department, day] += activity.EngageRead / 2;
            }
        }

        private void WriteCall(DemoUser user, int day)
        {
            var partners = _callPartners[day];
            int wanted = 1 + (int)(Random(user, day, 1010) % 3);
            var participants = new List<int>(3);
            int offset = partners.Count == 0 ? 0 : (int)(Random(user, day, 1011) % (uint)partners.Count);
            for (int i = 0; i < partners.Count && participants.Count < wanted; i++)
            {
                int id = partners[(offset + i) % partners.Count];
                if (id != user.Id) participants.Add(id);
            }
            int call = ++_calls;
            var start = Timestamp(user, day, 1012);
            // Bound the interval at UTC midnight even for late North American office hours.
            var end = start.AddMinutes(10 + Random(user, day, 1013) % 36);
            var last = _options.Start.AddDays(day + 1).AddSeconds(-1);
            if (end > last) end = last;
            _sink.Write(DemoTables.CallRecords, call, user.Id, participants.Count == 1 ? 1 : 2,
                DemoRandom.Id(_options.Seed, 1014, user.Id, day).ToString(), start, end);
            for (int i = 0; i < participants.Count; i++)
            {
                int session = ++_sessions;
                _sink.Write(DemoTables.CallSessions, session, participants[i],
                    start.AddSeconds(i * 5), end.AddSeconds(-i * 5), call);
                _sink.Write(DemoTables.CallSessionModalities, 1, session);
                if ((user.Id + day + i) % 3 != 0) _sink.Write(DemoTables.CallSessionModalities, 2, session);
                if (i == 0 && (user.Id + day) % 2 == 0) _sink.Write(DemoTables.CallSessionModalities, 3, session);
                if (i == 0 && Random(user, day, 1015) % 3 == 0)
                {
                    int rating = (int)(Random(user, day, 1016) % (uint)Ratings.Length);
                    _sink.Write(DemoTables.CallFeedback, Ratings[rating],
                        rating == 1 ? "Contoso demo: intermittent audio." : "Contoso demo: meeting feedback.",
                        participants[i], call);
                }
            }
            if (participants.Count > 0 && Random(user, day, 1017) % 13 == 0)
                _sink.Write(DemoTables.CallFailures, "mediaConnectivityFailure", "midCall", call);
        }

        private void WriteChannelActivity(DemoUser user, int day, DemoDay activity)
        {
            int channel = user.Department * 2 + (int)(Random(user, day, 1020) % 2);
            _channelMessages[channel, day] += Math.Max(1, activity.Messages / 4);
            if (Random(user, day, 1021) % 3 == 0)
                _sink.Write(DemoTables.ChannelReactions, 1 + (int)(Random(user, day, 1022) % 4),
                    user.Id, channel + 1, Timestamp(user, day, 1023));
        }

        private void WriteEmail(DemoUser user, int day, DemoDay activity)
        {
            int count = Math.Min(2, activity.Sent);
            for (int slot = 0; slot < count; slot++)
            {
                int id = ++_emails;
                uint draw = Random(user, day, 1030 + slot);
                _sink.Write(DemoTables.SentEmails, id, Subjects[draw % (uint)Subjects.Length],
                    _calendar.Timestamp(user.Zone, day, Random(user, day, 1032), slot),
                    "ContosoDemo-" + DemoRandom.Id(_options.Seed, 1033, user.Id, day, slot).ToString("N"),
                    Sentiment(draw), user.Id, user.Id);
                // Receiving an email is not evidence of a recipient action: inactive colleagues may receive mail.
                int recipient = _options.Users == 1 ? _options.Users + 1 : user.Id % _options.Users + 1;
                _sink.Write(DemoTables.EmailRecipients, id, recipient);
                if (draw % 2 == 0)
                    _sink.Write(DemoTables.EmailRecipients, id, _options.Users + 2);
            }
        }

        private void WritePageActivity(DemoUser user, int day, DemoDay activity)
        {
            int page = day % Math.Min(3, activity.SharePointFiles);
            int url = user.Department * 3 + page + 1;
            var time = Timestamp(user, day, 1040);
            if ((_likedPages & (1 << page)) == 0)
            {
                _sink.Write(DemoTables.PageLikes, user.Id, url, time, user.Id);
                _likedPages |= 1 << page;
            }
            uint draw = Random(user, day, 1041);
            if (draw % 5 != 0) return;
            int comment = ++_comments, text = (int)(draw % (uint)CommentTexts.Length);
            _sink.Write(DemoTables.PageComments, comment, CommentTexts[text],
                draw % 7 == 0 ? (object)null : text == 3 ? 3 : 1, Sentiment(Random(user, day, 1042)), null,
                user.Id, url, time, comment);
            if (draw % 4 == 0)
            {
                int reply = ++_comments;
                _sink.Write(DemoTables.PageComments, reply, "Contoso follow-up: adding a worked example.",
                    1, 0.8, comment, user.Id, url, time.AddSeconds(20), reply);
            }
        }

        private void WriteWebActivity(DemoUser user, int day, DemoDay activity)
        {
            int session = (user.Id - 1) * _options.Days + day + 1;
            // Reuse the base navigation path and timestamp, not an unrelated second set of sessions/hits.
            var start = _calendar.Timestamp(user.Zone, day, DemoRandom.Value(_options.Seed, user.Id, day, 90));
            int element = 1 + (int)(Random(user, day, 1052) % 3);
            _sink.Write(DemoTables.Clicks, user.Department * 3 + 2, element, element,
                checked((session - 1) * 3 + 1), start.AddSeconds(5));
            if (Random(user, day, 1050) % 3 == 0)
                _sink.Write(DemoTables.Searches, session, 1 + (int)(Random(user, day, 1051) % 5), start.AddSeconds(7));
        }

        public void WriteSummaries()
        {
            if (!_dimensionsWritten || _summariesWritten) throw new InvalidOperationException("Invalid collaboration summary order.");
            _summariesWritten = true;
            if (_options.Includes(DemoArea.Teams))
                for (int channel = 0; channel < _members.Length * 2; channel++)
                    for (int day = 0; day < _options.Days; day++)
                    {
                        int messages = _channelMessages[channel, day];
                        if (messages == 0) continue;
                        int id = channel * _options.Days + day + 1;
                        uint draw = DemoRandom.Value(_options.Seed, channel + 1, day, 1060);
                        var sentiment = Sentiment(draw);
                        _sink.Write(DemoTables.ChannelStats, id, messages, sentiment, _options.Start.AddDays(day), channel + 1);
                        if (sentiment == null) continue;
                        int keyword = (int)(draw % (uint)KeywordNames.Length);
                        _sink.Write(DemoTables.ChannelKeywords, keyword + 1, id, Math.Max(1, messages / 3));
                        _sink.Write(DemoTables.ChannelKeywords, (keyword + 1) % KeywordNames.Length + 1, id, Math.Max(1, messages / 5));
                        _sink.Write(DemoTables.ChannelLanguages, 1, id);
                        if (draw % 4 == 0) _sink.Write(DemoTables.ChannelLanguages, 2 + (int)(draw / 4 % 2), id);
                    }
            if (_options.Includes(DemoArea.Engage))
                for (int group = 0; group < _members.Length; group++)
                    for (int day = 0; day < _options.Days; day++)
                    {
                        if (_groupReads[group, day] == 0) continue;
                        var date = _options.Start.AddDays(day);
                        _sink.Write(DemoTables.EngageGroupActivity, _groupPosts[group, day], _groupReads[group, day],
                            _groupLikes[group, day], _members[group], group + 1, date, date);
                    }
        }

        private void WriteNames(DemoTable table, string[] names)
        {
            for (int i = 0; i < names.Length; i++) _sink.Write(table, i + 1, names[i]);
        }

        private uint Random(DemoUser user, int day, int salt) => DemoRandom.Value(_options.Seed, user.Id, day, salt);
        private DateTime Timestamp(DemoUser user, int day, int salt) => _calendar.Timestamp(user.Zone, day, Random(user, day, salt));
        private static object Sentiment(uint draw) => draw % 5 == 0 ? null : (object)(0.15 + draw % 8 * 0.1);
    }
}
