using Common.Entities.CopilotAdoption;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Tests.FakeDataGen.Seeding;

namespace Tests.FakeDataGen.Demo
{
    internal sealed class DemoSummary
    {
        public string FormatVersion { get; set; } = DemoOptions.FormatVersion;
        public string Fingerprint { get; set; }
        public string Status { get; set; }
        public int Users { get; set; }
        public int Skus { get; set; }
        public int Seed { get; set; }
        public DateTime AsOf { get; set; }
        public DateTime FirstActivityDate { get; set; }
        public DateTime LastReportDate { get; set; }
        public long ElapsedMilliseconds { get; set; }
        public long PeakWorkingSetBytes { get; set; }
        public long TotalRows => Rows.Values.Sum();
        public Dictionary<string, long> Rows { get; } = new Dictionary<string, long>(StringComparer.Ordinal);
        public Dictionary<string, int> Cohorts { get; } = new Dictionary<string, int>(StringComparer.Ordinal);
        public Dictionary<string, int> AdoptionBands { get; } = new Dictionary<string, int>(StringComparer.Ordinal);
        public Dictionary<string, int> CurrentSkuMembers { get; } = new Dictionary<string, int>(StringComparer.Ordinal);
        public int CompletedProfileWeeks { get; set; }
    }

    internal sealed class CountingDemoSink : IDemoSink
    {
        private readonly IDemoSink _inner;
        private readonly DemoSummary _summary;
        public CountingDemoSink(DemoSummary summary, IDemoSink inner = null) { _summary = summary; _inner = inner; }
        public void Write(DemoTable table, params object[] values)
        {
            table.ValidateValues(values);
            _inner?.Write(table, values);
            _summary.Rows.TryGetValue(table.Name, out long count);
            _summary.Rows[table.Name] = count + 1;
        }
        public void Flush() => _inner?.Flush();
        public void Dispose() => _inner?.Dispose();
    }

    internal sealed class DemoGenerator
    {
        private readonly DemoOptions _options;
        private readonly DemoPopulation _population;
        private readonly DemoCalendar _calendar;
        private readonly Dictionary<DemoTable, Dictionary<string, int>> _lookups = new Dictionary<DemoTable, Dictionary<string, int>>();
        private readonly int[,] _dailyActive;
        private readonly int[,] _rollingActive;
        private readonly long[] _dailyPrompts, _rollingPrompts;
        private readonly CancellationToken _cancellation;
        private IDemoSink _sink;
        private DemoSummary _summary;

        public DemoGenerator(DemoOptions options, CancellationToken cancellation = default(CancellationToken))
        {
            _options = options;
            _population = new DemoPopulation(options);
            _calendar = new DemoCalendar(options);
            _cancellation = cancellation;
            _dailyActive = new int[options.Days, DemoTimeline.ReportApps.Length];
            _rollingActive = new int[options.Days, DemoTimeline.ReportApps.Length];
            _dailyPrompts = new long[options.Days];
            _rollingPrompts = new long[options.Days];
        }

        public DemoSummary Generate(IDemoSink destination, DemoSummary summary, Action<string> progress)
        {
            _summary = summary;
            _sink = destination;
            progress?.Invoke("Writing synthetic dimensions, users and current licence assignments...");
            WriteDimensions();
            _sink.Flush();
            var managers = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int id = 1; id <= _options.Users; id++)
            {
                _cancellation.ThrowIfCancellationRequested();
                var user = _population.User(id);
                var p = user.Profile;
                string managerKey = p.Department + "|" + p.Company;
                int? manager = managers.TryGetValue(managerKey, out int leader) ? (int?)leader : null;
                if (!manager.HasValue) managers.Add(managerKey, id);
                _sink.Write(DemoTables.Users, id, user.Upn, user.Upn, DemoRandom.Id(_options.Seed, 1, id).ToString(),
                    p.AccountEnabled, _options.AsOf, p.PostalCode, Lookup(DemoTables.Departments, p.Department),
                    Lookup(DemoTables.Companies, p.Company), Lookup(DemoTables.Jobs, p.JobTitle),
                    Lookup(DemoTables.States, p.StateOrProvince), Lookup(DemoTables.Countries, p.Country),
                    Lookup(DemoTables.Offices, p.OfficeLocation), Lookup(DemoTables.UsageLocations, p.UsageLocation), manager);
                foreach (var sku in _population.Skus)
                    if (sku.Includes(id, _options.Users)) _sink.Write(DemoTables.Assignments, id, sku.Id);
                Increment(summary.Cohorts, user.Cohort.ToString());
            }
            foreach (var sku in _population.Skus) summary.CurrentSkuMembers.Add(sku.PartNumber, sku.Members);
            _sink.Flush();

            int progressEvery = Math.Max(1, _options.Users / 20);
            for (int id = 1; id <= _options.Users; id++)
            {
                _cancellation.ThrowIfCancellationRequested();
                var user = _population.User(id);
                var timeline = new DemoTimeline(_options, user);
                WriteTimeline(user, timeline);
                if (id % progressEvery == 0 || id == _options.Users)
                    progress?.Invoke($"Activity: {id:N0}/{_options.Users:N0} users; {summary.TotalRows:N0} source rows.");
            }
            WriteCopilotCounts();
            _sink.Flush();
            return summary;
        }

        private int Lookup(DemoTable table, string name) => _lookups[table][name];
        private void Dimension(DemoTable table, IEnumerable<string> names)
        {
            var values = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var name in names)
                if (!values.ContainsKey(name))
                {
                    int id = values.Count + 1;
                    values.Add(name, id);
                    _sink.Write(table, id, name);
                }
            _lookups.Add(table, values);
        }

        private void WriteDimensions()
        {
            Dimension(DemoTables.Departments, SeedDataCatalogue.Departments);
            Dimension(DemoTables.Jobs, SeedDataCatalogue.JobTitles);
            Dimension(DemoTables.Companies, SeedDataCatalogue.Companies);
            Dimension(DemoTables.States, SeedDataCatalogue.StatesOrProvinces);
            Dimension(DemoTables.Countries, SeedDataCatalogue.Countries);
            Dimension(DemoTables.Offices, SeedDataCatalogue.OfficeLocations);
            Dimension(DemoTables.UsageLocations, SeedDataCatalogue.UsageLocations);
            foreach (var sku in _population.Skus) _sink.Write(DemoTables.Licences, sku.Id, sku.Name, sku.PartNumber);
            _sink.Write(DemoTables.Operations, 1, "CopilotInteraction");
            _sink.Write(DemoTables.Operations, 2, "FileAccessed");
            _sink.Write(DemoTables.Operations, 3, "FileModified");
            _sink.Write(DemoTables.Agents, 1, "Contoso Knowledge Assistant", "Copilot.Studio.ContosoDemo.Knowledge", true);
            _sink.Write(DemoTables.Agents, 2, "Contoso Sales Coach", "Copilot.Studio.ContosoDemo.Sales", true);
            _sink.Write(DemoTables.Agents, 3, "Contoso Expenses Bot", "Copilot.Studio.ContosoDemo.Expenses", true);
            _sink.Write(DemoTables.Agents, 4, "Contoso Onboarding Guide", "Copilot.Studio.ContosoDemo.Onboarding", true);
            _sink.Write(DemoTables.Agents, 5, "Microsoft 365 Copilot Cowork", "Copilot.M365Copilot.CoworkAgent", false);
            Dimension(DemoTables.Titles, new[] { "Welcome", "Working together", "Καλημέρα κόσμε" });
            for (int site = 1; site <= SeedDataCatalogue.Departments.Length; site++)
            {
                string siteUrl = $"https://contoso.sharepoint.com/sites/demo-{site:D2}";
                _sink.Write(DemoTables.Sites, site, siteUrl, DemoRandom.Id(_options.Seed, 2, site).ToString());
                _sink.Write(DemoTables.Webs, site, siteUrl, "Contoso " + SeedDataCatalogue.Departments[site - 1], site);
                for (int page = 0; page < 3; page++)
                {
                    int key = (site - 1) * 3 + page + 1;
                    _sink.Write(DemoTables.Urls, key, siteUrl + "/SitePages/" + (page == 2 ? "Καλημέρα-κόσμε/" : "")
                        + $"Contoso-demo-page-{page + 1}.aspx");
                }
            }
            _sink.Write(DemoTables.Extensions, 1, "aspx");
            for (int page = 1; page <= 3; page++)
                _sink.Write(DemoTables.FileNames, page, $"Contoso-demo-page-{page}.aspx");
            _sink.Write(DemoTables.ItemTypes, 1, "File");
            _sink.Write(DemoTables.Browsers, 1, "Edge");
            _sink.Write(DemoTables.Browsers, 2, "Chrome");
            _sink.Write(DemoTables.Browsers, 3, "Safari");
            _sink.Write(DemoTables.Devices, 1, "Desktop");
            _sink.Write(DemoTables.Devices, 2, "Mobile");
            _sink.Write(DemoTables.OperatingSystems, 1, "Windows");
            _sink.Write(DemoTables.OperatingSystems, 2, "macOS");
            _sink.Write(DemoTables.OperatingSystems, 3, "iOS");
            _sink.Write(DemoTables.OperatingSystems, 4, "Android");
            Dimension(DemoTables.WebCountries, SeedDataCatalogue.Countries);
            Dimension(DemoTables.WebCities, SeedDataCatalogue.Locales.Select(l => WebCity(l.City)));
            _sink.Write(DemoTables.ResourceNames, 1, "Contoso knowledge – Καλημέρα κόσμε");
            _sink.Write(DemoTables.ResourceSites, 1, "https://contoso.sharepoint.com/sites/demo-01");
            _sink.Write(DemoTables.ResourceTypes, 1, "SharePoint");
            _sink.Write(DemoTables.InteractionTypes, 1, "userPrompt");
            _sink.Write(DemoTables.InteractionTypes, 2, "aiResponse");
            for (int i = 0; i < DemoTimeline.Hosts.Length; i++)
                _sink.Write(DemoTables.InteractionApps, i + 1, "IPM.SkypeTeams.Message.Copilot." + DemoTimeline.Hosts[i]);
            _sink.Write(DemoTables.ConversationTypes, 1, "bizchat");
            _sink.Write(DemoTables.ConversationTypes, 2, "appchat");
        }

        private void WriteTimeline(DemoUser user, DemoTimeline timeline)
        {
            var lastWorkload = new DateTime?[5];
            DateTime? lastOffice = null, lastTeamsDevice = null;
            var lastApps = new DateTime?[9];
            var appDays = new int[28, 9];
            var appWindow = new int[9];
            var turnWindow = new int[28];
            var chatWindow = new int[28];
            int windowTurns = 0, windowChat = 0;
            int interactions = 0, prior = 0, active = 0;
            DateTime? first = null, last = null;
            var hosts = new HashSet<int>();
            for (int d = 0; d < timeline.Days.Length; d++)
            {
                if (d % 28 == 0) _cancellation.ThrowIfCancellationRequested();
                var date = _options.Start.AddDays(d);
                var day = timeline.Days[d];
                int bucket = d % 28;
                for (int app = 0; app < 9; app++)
                {
                    appWindow[app] -= appDays[bucket, app];
                    appDays[bucket, app] = 0;
                }
                windowTurns -= turnWindow[bucket];
                windowChat -= chatWindow[bucket];
                turnWindow[bucket] = day.CopilotTurns;
                chatWindow[bucket] = 0;
                if (day.CopilotTurns > 0)
                {
                    appDays[bucket, 0] = 1;
                    lastApps[0] = date;
                    if (!first.HasValue) first = date;
                    last = date;
                    if (d >= _options.Days - 28) { active++; interactions += day.CopilotTurns; }
                    else prior += day.CopilotTurns;
                    for (int slot = 0; slot < day.CopilotTurns; slot++)
                    {
                        int host = timeline.HostIndex(d, slot);
                        int app = host == 7 ? 8 : host + 1;
                        appDays[bucket, app] = 1;
                        lastApps[app] = date;
                        if (host == 0) chatWindow[bucket]++;
                        if (timeline.Agent(d, slot) > 0) { appDays[bucket, 8] = 1; lastApps[8] = date; }
                        if (d >= _options.Days - 28) hosts.Add(host);
                    }
                    WriteCopilotEvents(user, timeline, d);
                }
                windowTurns += turnWindow[bucket];
                windowChat += chatWindow[bucket];
                for (int app = 0; app < 9; app++)
                {
                    appWindow[app] += appDays[bucket, app];
                    if (user.CopilotLicensed)
                    {
                        _dailyActive[d, app] += appDays[bucket, app];
                        if (appWindow[app] > 0) _rollingActive[d, app]++;
                    }
                }
                if (user.CopilotLicensed)
                {
                    _dailyPrompts[d] += day.CopilotTurns;
                    _rollingPrompts[d] += windowTurns;
                }
                if (day.SharePointFiles > 0) WriteWebEvents(user, d, day);
                if (date > _options.ReportEnd) continue;
                int appMask = 0;
                for (int app = 1; app <= 7; app++) if (appDays[bucket, app] > 0) appMask |= 1 << app;
                WriteWorkloadRows(user, d, day, lastWorkload, appMask, ref lastOffice, ref lastTeamsDevice);
                if (user.CopilotLicensed && date >= _options.FirstCopilotReport)
                    _sink.Write(DemoTables.CopilotUsage, user.Id, date, lastApps[0], 28, windowTurns, windowChat, 0,
                        appWindow[0], lastApps[1], lastApps[2], lastApps[3], lastApps[4], lastApps[5], lastApps[6],
                        lastApps[7], lastApps[1], lastApps[8], false);
            }
            if (user.CopilotLicensed)
            {
                var scored = CopilotAdoptionScoring.Score(new LicensedUserUsageRow
                {
                    UserId = user.Id, ActiveDays = active, Interactions = interactions, PriorInteractions = prior,
                    AppsUsed = hosts.Count, FirstInteractionUtc = first, LastInteractionUtc = last
                }, _options.AsOf.AddDays(-28), _options.AsOf, auditAvailable: true);
                Increment(_summary.AdoptionBands, scored.BandName);
            }
        }

        private void WriteWorkloadRows(DemoUser user, int d, DemoDay day, DateTime?[] last, int copilotApps,
            ref DateTime? lastOffice, ref DateTime? lastTeamsDevice)
        {
            var date = _options.Start.AddDays(d);
            int[] totals = { day.Messages + day.Meetings, day.Sent + day.Read, day.SharePointFiles, day.OneDriveFiles, day.EngageRead };
            for (int i = 0; i < last.Length; i++) if (totals[i] > 0) last[i] = date;
            int privateChats = day.Messages / 2, posts = day.Messages / 8, replies = day.Messages / 4;
            int organised = day.Meetings / 3, attended = day.Meetings - organised;
            _sink.Write(DemoTables.Teams, user.Id, date, last[0],
                privateChats, day.Messages - privateChats - posts - replies, posts, replies, day.Messages / 30,
                day.Messages / 40, day.Meetings, attended / 3, organised / 3, attended, organised,
                attended / 3, organised / 3, attended - 2 * (attended / 3), organised - 2 * (organised / 3),
                day.Meetings * 1500 + day.Messages / 40 * 480, day.Meetings * 900, day.Meetings * 300);
            _sink.Write(DemoTables.Outlook, user.Id, date, last[1], day.Sent, day.Received, day.Read,
                day.Sent > 0 ? organised : 0, day.Sent > 0 ? day.Meetings : 0);
            _sink.Write(DemoTables.SharePoint, user.Id, date, last[2], day.SharePointFiles, day.SharePointFiles / 5,
                day.SharePointFiles / 6, day.SharePointFiles / 30);
            _sink.Write(DemoTables.OneDrive, user.Id, date, last[3], day.OneDriveFiles, day.OneDriveFiles / 3,
                day.OneDriveFiles / 8, day.OneDriveFiles / 30);
            _sink.Write(DemoTables.Engage, user.Id, date, last[4], day.EngageRead / 8, day.EngageRead, day.EngageRead / 4);

            bool teams = totals[0] > 0 || (copilotApps & (1 << 2)) != 0;
            bool engage = day.EngageRead > 0, mac = user.Id % 5 == 0, mobile = user.Id % 3 == 0;
            if (teams) lastTeamsDevice = date;
            _sink.Write(DemoTables.TeamsDevices, user.Id, date, lastTeamsDevice,
                teams, false, false, false, teams && mobile && mac, teams && mobile && !mac, teams && mac, teams && !mac);
            _sink.Write(DemoTables.EngageDevices, user.Id, date, last[4], engage, false,
                engage && mobile && !mac, false, engage && mobile && mac, false);
            bool files = day.SharePointFiles + day.OneDriveFiles > 0;
            bool[] apps =
            {
                day.Sent > 0 || (copilotApps & (1 << 4)) != 0,
                files || (copilotApps & (1 << 3)) != 0,
                (files && user.Id % 3 != 0) || (copilotApps & (1 << 5)) != 0,
                (files && user.Id % 4 == 0) || (copilotApps & (1 << 6)) != 0,
                (files && user.Id % 5 == 0) || (copilotApps & (1 << 7)) != 0,
                teams
            };
            bool anyApp = apps.Any(a => a);
            if (anyApp) lastOffice = date;
            var values = new List<object> { user.Id, date, lastOffice };
            values.AddRange(new object[] { anyApp && !mac, anyApp && mac, anyApp && mobile, anyApp });
            values.AddRange(apps.Cast<object>());
            foreach (bool device in new[] { !mac, mac, mobile, true })
                values.AddRange(apps.Select(a => (object)(a && device)));
            _sink.Write(DemoTables.Platforms, values.ToArray());
        }

        private void WriteCopilotEvents(DemoUser user, DemoTimeline timeline, int dayIndex)
        {
            var day = timeline.Days[dayIndex];
            int sessionId = (user.Id - 1) * _options.Days + dayIndex + 1;
            string thread = "contoso-demo-" + DemoRandom.Id(_options.Seed, 3, user.Id, dayIndex).ToString("N");
            if (user.CopilotLicensed) _sink.Write(DemoTables.InteractionSessions, sessionId, thread, user.Id);
            for (int slot = 0; slot < day.CopilotTurns; slot++)
            {
                var id = DemoRandom.Id(_options.Seed, 4, user.Id, dayIndex, slot);
                int host = timeline.HostIndex(dayIndex, slot), agent = timeline.Agent(dayIndex, slot);
                var time = _calendar.Timestamp(user.Zone, dayIndex, DemoRandom.Value(_options.Seed, user.Id, dayIndex, 50), slot);
                _sink.Write(DemoTables.Audit, id, user.Id, 1, time);
                _sink.Write(DemoTables.Chats, id, DemoTimeline.Hosts[host], agent == 0 ? (object)null : agent,
                    thread, user.Profile.UsageLocation, DemoOptions.FormatVersion, user.Id, time);
                if (agent > 0 && agent != 5 && slot == 0)
                    _sink.Write(DemoTables.Resources, id, 1, 1, 1);
                // Graph interaction history requires a Copilot licence. Free Chat demand is
                // visible in the audit source, but must not be invented in that Graph feed.
                if (!user.CopilotLicensed) continue;
                int words = 8 + (int)(DemoRandom.Value(_options.Seed, user.Id, dayIndex, 51 + slot) % 50);
                int latency = 800 + (int)(DemoRandom.Value(_options.Seed, user.Id, dayIndex, 71 + slot) % 15000);
                string request = id.ToString("N");
                _sink.Write(DemoTables.Interactions, request + "-prompt", sessionId, user.Id, request, 1,
                    host + 1, host == 0 ? 1 : 2, time, words * 6, words, 0, 0, 0, agent > 0 ? 1 : 0, null);
                _sink.Write(DemoTables.Interactions, request + "-response", sessionId, user.Id, request, 2,
                    host + 1, host == 0 ? 1 : 2, time.AddMilliseconds(latency), words * 24, words * 4, 0, agent > 0 ? 1 : 0, 0, 0, latency);
            }
        }

        private void WriteWebEvents(DemoUser user, int day, DemoDay activity)
        {
            int session = (user.Id - 1) * _options.Days + day + 1, site = user.Department + 1;
            _sink.Write(DemoTables.Sessions, session, DemoRandom.Id(_options.Seed, 5, user.Id, day).ToString("N"), user.Id);
            var start = _calendar.Timestamp(user.Zone, day, DemoRandom.Value(_options.Seed, user.Id, day, 90));
            // A small representative navigation path, not a second copy of every Graph file count.
            for (int page = 0; page < Math.Min(3, activity.SharePointFiles); page++)
            {
                int url = (site - 1) * 3 + page + 1;
                var stamp = start.AddSeconds(page * 12);
                var eventId = DemoRandom.Id(_options.Seed, 6, user.Id, day, page);
                _sink.Write(DemoTables.Audit, eventId, user.Id, page == 2 ? 3 : 2, stamp);
                _sink.Write(DemoTables.SharePointAudit, eventId, url, 1, page + 1, site, 1);
                bool mac = user.Id % 5 == 0, mobile = user.Id % 3 == 0;
                _sink.Write(DemoTables.Hits, url, stamp, session, page + 1, site, mac ? 3 : 1 + user.Id % 2,
                    mobile ? 2 : 1, mobile ? (mac ? 3 : 4) : mac ? 2 : 1, 10.0 + page, 350.0 + user.Id % 500,
                    DemoRandom.Id(_options.Seed, 7, user.Id, day, page),
                    Lookup(DemoTables.WebCountries, user.Profile.Country), Lookup(DemoTables.WebCities, WebCity(user.Profile.City)));
            }
        }

        private void WriteCopilotCounts()
        {
            int licensed = _population.Skus[1].Members;
            for (int d = 0; d < _options.Days && _options.Start.AddDays(d) <= _options.ReportEnd; d++)
            {
                var date = _options.Start.AddDays(d);
                for (int app = 0; app < DemoTimeline.ReportApps.Length; app++)
                {
                    _sink.Write(DemoTables.CopilotCounts, _options.ReportEnd, date, "Trend", null,
                        DemoTimeline.ReportApps[app], licensed, _dailyActive[d, app], app == 0 ? (object)_dailyPrompts[d] : null, null);
                    if (date >= _options.FirstCopilotReport)
                        _sink.Write(DemoTables.CopilotCounts, date, date, "Summary", 28,
                            DemoTimeline.ReportApps[app], licensed, _rollingActive[d, app],
                            app == 0 ? (object)_rollingPrompts[d] : null,
                            app == 0 && _rollingActive[d, 0] > 0 ? (object)((double)_rollingPrompts[d] / _rollingActive[d, 0]) : null);
                }
            }
        }

        private static void Increment(Dictionary<string, int> values, string name)
        {
            values.TryGetValue(name, out int n);
            values[name] = n + 1;
        }

        // Legacy web-traffic city names are varchar; do not pretend that boundary is Unicode.
        // The user-metadata and URL/title dimensions retain the catalogue's actual Unicode samples.
        private static string WebCity(string city) => city.Replace("São Paulo", "Sao Paulo").Replace("Zürich", "Zurich");
    }
}
