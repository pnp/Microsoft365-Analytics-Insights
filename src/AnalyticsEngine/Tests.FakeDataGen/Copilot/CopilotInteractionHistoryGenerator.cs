using Common.Entities;
using Common.Entities.Entities.Copilot;
using System;
using System.Collections.Generic;
using System.Linq;
using Tests.FakeDataGen.Generation;

namespace Tests.FakeDataGen.Copilot
{
    /// <summary>
    /// Generates fake Copilot AI interaction history - the per-turn "prompt history" tables the
    /// interaction-history import fills - so reports can be built and measured without a real tenant.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The point of this generator is report shape, so it produces the structure real reporting depends on
    /// rather than uniform noise:
    /// </para>
    /// <list type="bullet">
    /// <item><description><b>Real turns.</b> Every user prompt is followed by an <c>aiResponse</c> sharing its
    /// <c>request_id</c>, which is what makes turn counts and prompt-to-response ratios meaningful. The
    /// response carries <c>response_latency_ms</c>; the prompt does not - matching what the importer
    /// stores.</description></item>
    /// <item><description><b>Prompt-only enrichment.</b> Sentiment, language and key phrases are set on
    /// <c>userPrompt</c> rows only. Copilot responses are never scored by the importer, so a report that
    /// averaged sentiment across all rows would look right here and be wrong in production.</description></item>
    /// <item><description><b>Shared threads.</b> A small share of sessions belong to two users with the same
    /// <c>session_ref</c> - a Teams meeting Copilot session appears in more than one participant's history.
    /// That is why the sessions table is unique on (user, ref) rather than ref alone, and it is exactly the
    /// case a per-user report can get wrong.</description></item>
    /// <item><description><b>Skew.</b> App class, device and locale are weighted, and turns per session vary,
    /// so "top N" style reports have something to rank.</description></item>
    /// </list>
    /// <para>
    /// Locales and key phrases deliberately include Greek. Any column holding customer text must survive the
    /// full Unicode range, and generating only ASCII here would hide a truncation or collation bug until a
    /// customer found it.
    /// </para>
    /// <para>
    /// All generated content is synthetic. No prompt or response text is produced or stored - the real import
    /// stores only counts, so this generates only counts.
    /// </para>
    /// </remarks>
    public class CopilotInteractionHistoryGenerator
    {
        private readonly string _connectionString;
        private readonly Random _random = new Random();
        private readonly CopilotLicenseManager _licenseManager;
        private readonly CopilotUserManager _userManager;

        /// <summary>Sessions per context. EF6 slows down as the change tracker grows, so it is recycled.</summary>
        private const int SessionsPerBatch = 200;

        public CopilotInteractionHistoryGenerator(string connectionString)
        {
            _connectionString = connectionString;
            _licenseManager = new CopilotLicenseManager();
            _userManager = new CopilotUserManager(_random, _licenseManager);
        }

        #region Weighted catalogues

        // Weighted so reports have something to rank. BizChat dominates on a real tenant.
        private static readonly (string Name, int Weight)[] AppClasses =
        {
            ("IPM.SkypeTeams.Message.Copilot.BizChat", 45),
            ("IPM.SkypeTeams.Message.Copilot.Teams", 15),
            ("IPM.SkypeTeams.Message.Copilot.Word", 12),
            ("IPM.SkypeTeams.Message.Copilot.Outlook", 12),
            ("IPM.SkypeTeams.Message.Copilot.Excel", 8),
            ("IPM.SkypeTeams.Message.Copilot.PowerPoint", 5),
            ("IPM.SkypeTeams.Message.Copilot.Loop", 3),
        };

        private static readonly (string Name, int Weight)[] Devices =
        {
            ("desktop", 55),
            ("web", 30),
            ("mobile", 15),
        };

        // el-GR is here on purpose: non-Latin script must survive the round trip.
        private static readonly (string Name, int Weight)[] Locales =
        {
            ("en-us", 55),
            ("en-gb", 15),
            ("fr-fr", 8),
            ("de-de", 7),
            ("es-es", 5),
            ("el-gr", 5),
            ("ja-jp", 5),
        };

        private static readonly (string Locale, string Language)[] LanguageByLocale =
        {
            ("en-us", "English"),
            ("en-gb", "English"),
            ("fr-fr", "French"),
            ("de-de", "German"),
            ("es-es", "Spanish"),
            ("el-gr", "Greek"),
            ("ja-jp", "Japanese"),
        };

        private const string ConversationTypeBizChat = "bizchat";
        private const string ConversationTypeAppChat = "appchat";
        private const string InteractionTypePrompt = "userPrompt";
        private const string InteractionTypeResponse = "aiResponse";

        /// <summary>
        /// Topical phrases of the kind Azure AI Language extracts - never prompt text. The Greek entries make
        /// sure a non-ASCII key phrase survives insertion, indexing and reporting.
        /// </summary>
        private static readonly string[] KeyPhrases =
        {
            "quarterly sales forecast", "customer churn analysis", "meeting summary", "expense policy",
            "onboarding checklist", "budget variance", "project timeline", "risk register",
            "security incident report", "supplier contract", "travel booking", "performance review",
            "product roadmap", "release notes", "training plan", "headcount planning",
            "invoice reconciliation", "market research", "sprint retrospective", "data retention policy",
            "Καλημέρα κόσμε", "ετήσιος προϋπολογισμός", "ανάλυση πωλήσεων",
        };

        #endregion

        /// <summary>
        /// Generates interaction history and the per-user watermarks / import-log rows that go with it.
        /// </summary>
        /// <param name="userCount">Users to spread the history across (existing users are reused).</param>
        /// <param name="sessionsPerUser">Average conversations per user.</param>
        /// <param name="turnsPerSession">Average prompt+response turns per conversation. Each turn is 2 rows.</param>
        /// <param name="daysBack">Window the history is spread over.</param>
        /// <param name="cognitivePercentage">Share of prompts carrying sentiment / language / key phrases.</param>
        /// <param name="sharedThreadPercentage">Share of sessions also present in a second user's history.</param>
        /// <param name="windowEndUtc">Optional shared window end, so several generators can align.</param>
        public void GenerateInteractionHistory(
            int userCount = 250,
            int sessionsPerUser = 8,
            int turnsPerSession = 6,
            int daysBack = 90,
            int cognitivePercentage = 70,
            int sharedThreadPercentage = 5,
            DateTime? windowEndUtc = null)
        {
            if (userCount < 1) throw new ArgumentOutOfRangeException(nameof(userCount));
            if (sessionsPerUser < 1) throw new ArgumentOutOfRangeException(nameof(sessionsPerUser));
            if (turnsPerSession < 1) throw new ArgumentOutOfRangeException(nameof(turnsPerSession));
            if (daysBack < 1) throw new ArgumentOutOfRangeException(nameof(daysBack));
            if (cognitivePercentage < 0 || cognitivePercentage > 100) throw new ArgumentOutOfRangeException(nameof(cognitivePercentage));
            if (sharedThreadPercentage < 0 || sharedThreadPercentage > 100) throw new ArgumentOutOfRangeException(nameof(sharedThreadPercentage));

            var windowEnd = windowEndUtc ?? DateTime.UtcNow;
            var runStarted = DateTime.UtcNow;

            Console.WriteLine("Generating Copilot AI interaction history...");
            Console.WriteLine($"- {userCount} user(s), ~{sessionsPerUser} session(s) each, ~{turnsPerSession} turn(s) per session");
            Console.WriteLine($"- Spread across the last {daysBack} day(s)");
            Console.WriteLine($"- {cognitivePercentage}% of prompts will carry sentiment / language / key phrases");
            Console.WriteLine($"- {sharedThreadPercentage}% of sessions will be shared with a second user");
            Console.WriteLine($"- Estimated rows: ~{(long)userCount * sessionsPerUser * turnsPerSession * 2:N0} interactions");
            Console.WriteLine();

            List<int> userIds;
            Lookups lookups;

            using (var db = new AnalyticsEntitiesContext(_connectionString, true, false))
            {
                _licenseManager.EnsureLicensesExist(db);

                var users = db.users.OrderBy(u => u.ID).Take(userCount).ToList();
                if (users.Count == 0)
                {
                    Console.WriteLine($"No users found in database. Creating {userCount} test users...");
                    users = _userManager.CreateTestUsers(db, userCount, 100);
                }
                else
                {
                    Console.WriteLine($"Found {users.Count} existing user(s) to attribute history to.");
                }

                userIds = users.Select(u => u.ID).ToList();
                lookups = EnsureLookups(db);
            }

            if (userIds.Count == 0)
            {
                Console.WriteLine("No users available - nothing generated.");
                return;
            }

            var plan = BuildSessionPlan(userIds, sessionsPerUser, sharedThreadPercentage);
            Console.WriteLine($"Planned {plan.Count:N0} session(s) across {userIds.Count:N0} user(s).");

            var stats = new GenerationStats();
            var newestInteractionByUser = new Dictionary<int, DateTime>();

            for (int offset = 0; offset < plan.Count; offset += SessionsPerBatch)
            {
                var batch = plan.GetRange(offset, Math.Min(SessionsPerBatch, plan.Count - offset));
                WriteSessionBatch(batch, lookups, turnsPerSession, daysBack, cognitivePercentage,
                    windowEnd, stats, newestInteractionByUser);

                Console.WriteLine($"  {Math.Min(offset + SessionsPerBatch, plan.Count):N0}/{plan.Count:N0} session(s), " +
                    $"{stats.Interactions:N0} interaction(s) written...");
            }

            WriteWatermarks(newestInteractionByUser, userIds, windowEnd);
            WriteImportLog(runStarted, userIds.Count, newestInteractionByUser.Count, stats);

            Console.WriteLine();
            Console.WriteLine("Copilot interaction history generation complete:");
            Console.WriteLine($"  Sessions:      {stats.Sessions:N0}");
            Console.WriteLine($"  Interactions:  {stats.Interactions:N0} ({stats.Prompts:N0} prompt(s), {stats.Responses:N0} response(s))");
            Console.WriteLine($"  Key phrases:   {stats.KeyPhraseLinks:N0} link(s)");
            Console.WriteLine($"  Scored prompts:{stats.ScoredPrompts,8:N0}");
        }

        #region Lookups

        private Lookups EnsureLookups(AnalyticsEntitiesContext db)
        {
            var lookups = new Lookups
            {
                InteractionTypes = EnsureNamed(db, db.CopilotInteractionTypes,
                    new[] { InteractionTypePrompt, InteractionTypeResponse }),
                AppClasses = EnsureNamed(db, db.CopilotInteractionAppClasses, AppClasses.Select(a => a.Name)),
                ConversationTypes = EnsureNamed(db, db.CopilotInteractionConversationTypes,
                    new[] { ConversationTypeBizChat, ConversationTypeAppChat }),
                Locales = EnsureNamed(db, db.CopilotInteractionLocales, Locales.Select(l => l.Name)),
                Devices = EnsureNamed(db, db.CopilotInteractionDevices, Devices.Select(d => d.Name)),
                Languages = EnsureNamed(db, db.Languages, LanguageByLocale.Select(l => l.Language).Distinct()),
                KeyPhrases = EnsureNamed(db, db.KeyWords, KeyPhrases),
            };

            return lookups;
        }

        /// <summary>
        /// Resolves a name-keyed lookup table, inserting anything missing. Matches the importer's behaviour,
        /// including reusing the shared <c>languages</c> and <c>keywords</c> tables rather than duplicating them.
        /// </summary>
        private static Dictionary<string, int> EnsureNamed<T>(
            AnalyticsEntitiesContext db,
            System.Data.Entity.DbSet<T> set,
            IEnumerable<string> names) where T : Common.Entities.AbstractEFEntityWithName, new()
        {
            var wanted = names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var existing = set.Where(x => wanted.Contains(x.Name)).ToList();

            var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in existing)
            {
                if (!string.IsNullOrEmpty(row.Name))
                    byName[row.Name] = row.ID;
            }

            var added = new List<T>();
            foreach (var name in wanted)
            {
                if (byName.ContainsKey(name))
                    continue;

                var entity = new T { Name = name };
                set.Add(entity);
                added.Add(entity);
            }

            if (added.Count > 0)
            {
                db.SaveChanges();
                foreach (var entity in added)
                    byName[entity.Name] = entity.ID;
            }

            return byName;
        }

        #endregion

        #region Session planning and writing

        /// <summary>
        /// Decides who owns which conversation before anything is written, so shared threads can be planned
        /// rather than discovered.
        /// </summary>
        private List<PlannedSession> BuildSessionPlan(List<int> userIds, int sessionsPerUser, int sharedThreadPercentage)
        {
            var plan = new List<PlannedSession>(userIds.Count * sessionsPerUser);

            foreach (var userId in userIds)
            {
                // Vary the count so "most active users" reports have a real distribution to rank.
                int sessions = Math.Max(1, (int)Math.Round(sessionsPerUser * (0.4 + _random.NextDouble() * 1.6)));

                for (int i = 0; i < sessions; i++)
                {
                    var sessionRef = $"session-{Guid.NewGuid():N}";
                    plan.Add(new PlannedSession { UserId = userId, SessionRef = sessionRef });

                    // A shared thread is the SAME session_ref owned by a second user - a meeting Copilot
                    // session showing up in another participant's history.
                    if (userIds.Count > 1 && _random.Next(100) < sharedThreadPercentage)
                    {
                        int otherUserId;
                        do { otherUserId = userIds[_random.Next(userIds.Count)]; }
                        while (otherUserId == userId);

                        plan.Add(new PlannedSession { UserId = otherUserId, SessionRef = sessionRef });
                    }
                }
            }

            return plan;
        }

        private void WriteSessionBatch(
            List<PlannedSession> batch,
            Lookups lookups,
            int turnsPerSession,
            int daysBack,
            int cognitivePercentage,
            DateTime windowEnd,
            GenerationStats stats,
            Dictionary<int, DateTime> newestInteractionByUser)
        {
            // A fresh context per batch: EF6's change tracker makes inserts progressively slower as it grows,
            // and recycling is cheaper than fighting it.
            using (var db = new AnalyticsEntitiesContext(_connectionString, true, false))
            {
                db.Configuration.AutoDetectChangesEnabled = false;
                try
                {
                    var sessions = new List<CopilotInteractionSession>(batch.Count);
                    foreach (var planned in batch)
                    {
                        var session = new CopilotInteractionSession
                        {
                            SessionRef = planned.SessionRef,
                            UserId = planned.UserId
                        };
                        db.CopilotInteractionSessions.Add(session);
                        sessions.Add(session);
                    }

                    db.ChangeTracker.DetectChanges();
                    db.SaveChanges();
                    stats.Sessions += sessions.Count;

                    var keyPhraseLinks = new List<CopilotInteractionKeyword>();

                    for (int i = 0; i < sessions.Count; i++)
                    {
                        WriteSessionInteractions(db, sessions[i], lookups, turnsPerSession, daysBack,
                            cognitivePercentage, windowEnd, stats, newestInteractionByUser, keyPhraseLinks);
                    }

                    db.ChangeTracker.DetectChanges();
                    db.SaveChanges();

                    // Key-phrase links need the interaction ids, so they are a second pass.
                    foreach (var link in keyPhraseLinks)
                        db.CopilotInteractionKeywords.Add(link);

                    if (keyPhraseLinks.Count > 0)
                    {
                        db.ChangeTracker.DetectChanges();
                        db.SaveChanges();
                        stats.KeyPhraseLinks += keyPhraseLinks.Count;
                    }
                }
                finally
                {
                    db.Configuration.AutoDetectChangesEnabled = true;
                }
            }
        }

        private void WriteSessionInteractions(
            AnalyticsEntitiesContext db,
            CopilotInteractionSession session,
            Lookups lookups,
            int turnsPerSession,
            int daysBack,
            int cognitivePercentage,
            DateTime windowEnd,
            GenerationStats stats,
            Dictionary<int, DateTime> newestInteractionByUser,
            List<CopilotInteractionKeyword> keyPhraseLinks)
        {
            int turns = Math.Max(1, (int)Math.Round(turnsPerSession * (0.3 + _random.NextDouble() * 1.7)));

            // A conversation happens in one sitting: pick a start, then walk forward.
            var conversationStart = ActivityTimestampGenerator.Next(_random, daysBack, windowEnd);

            // Properties that belong to the conversation, not the turn.
            var appClass = WeightedPick(AppClasses);
            var locale = WeightedPick(Locales);
            var device = WeightedPick(Devices);
            var conversationType = appClass.Contains("BizChat") ? ConversationTypeBizChat : ConversationTypeAppChat;
            var languageName = LanguageByLocale.First(l => l.Locale == locale).Language;

            var cursor = conversationStart;

            for (int turn = 0; turn < turns; turn++)
            {
                var requestId = $"request-{Guid.NewGuid():N}";
                bool scored = _random.Next(100) < cognitivePercentage;

                // ---- The user prompt -------------------------------------------------------------
                int promptChars = 25 + _random.Next(375);
                var prompt = new CopilotInteraction
                {
                    GraphInteractionId = $"interaction-{Guid.NewGuid():N}",
                    SessionId = session.ID,
                    UserId = session.UserId,
                    RequestId = requestId,
                    InteractionTypeId = lookups.InteractionTypes[InteractionTypePrompt],
                    AppClassId = lookups.AppClasses[appClass],
                    ConversationTypeId = lookups.ConversationTypes[conversationType],
                    LocaleId = lookups.Locales[locale],
                    DeviceId = lookups.Devices[device],
                    CreatedUtc = cursor,
                    BodyCharCount = promptChars,
                    BodyWordCount = Math.Max(1, promptChars / 6),
                    AttachmentCount = _random.Next(100) < 12 ? 1 + _random.Next(2) : 0,
                    LinkCount = _random.Next(100) < 18 ? 1 + _random.Next(3) : 0,
                    MentionCount = _random.Next(100) < 8 ? 1 : 0,
                    ContextCount = _random.Next(100) < 35 ? 1 + _random.Next(4) : 0,

                    // Latency belongs to the response, never the prompt - see the entity remarks.
                    ResponseLatencyMs = null,

                    // Enrichment is prompt-only: the importer never scores Copilot's own output.
                    SentimentScore = scored ? Math.Round(_random.NextDouble(), 4) : (double?)null,
                    LanguageId = scored ? lookups.Languages[languageName] : (int?)null,
                };
                db.CopilotInteractions.Add(prompt);
                stats.Prompts++;
                stats.Interactions++;
                if (scored) stats.ScoredPrompts++;

                // ---- Copilot's response ----------------------------------------------------------
                // Mostly quick, with a long tail so latency percentile reports have something to show.
                int latencyMs = _random.Next(100) < 90
                    ? 500 + _random.Next(4500)
                    : 5000 + _random.Next(25000);

                var responseAt = cursor.AddMilliseconds(latencyMs);
                int responseChars = 200 + _random.Next(3800);

                var response = new CopilotInteraction
                {
                    GraphInteractionId = $"interaction-{Guid.NewGuid():N}",
                    SessionId = session.ID,
                    UserId = session.UserId,
                    RequestId = requestId,
                    InteractionTypeId = lookups.InteractionTypes[InteractionTypeResponse],
                    AppClassId = lookups.AppClasses[appClass],
                    ConversationTypeId = lookups.ConversationTypes[conversationType],
                    LocaleId = lookups.Locales[locale],
                    DeviceId = lookups.Devices[device],
                    CreatedUtc = responseAt,
                    BodyCharCount = responseChars,
                    BodyWordCount = Math.Max(1, responseChars / 6),
                    AttachmentCount = 0,
                    LinkCount = _random.Next(100) < 45 ? 1 + _random.Next(5) : 0,
                    MentionCount = 0,
                    ContextCount = 0,
                    ResponseLatencyMs = latencyMs,
                    SentimentScore = null,
                    LanguageId = null,
                };
                db.CopilotInteractions.Add(response);
                stats.Responses++;
                stats.Interactions++;

                if (scored)
                {
                    foreach (var phrase in PickKeyPhrases(locale))
                    {
                        keyPhraseLinks.Add(new CopilotInteractionKeyword
                        {
                            Interaction = prompt,
                            KeyWordId = lookups.KeyPhrases[phrase]
                        });
                    }
                }

                TrackNewest(newestInteractionByUser, session.UserId, responseAt);

                // Thinking time before the next prompt in the same conversation.
                cursor = responseAt.AddSeconds(15 + _random.Next(240));
            }
        }

        /// <summary>Greek conversations get Greek key phrases, so non-ASCII reaches the keywords table.</summary>
        private IEnumerable<string> PickKeyPhrases(string locale)
        {
            bool greek = locale == "el-gr";
            var pool = greek
                ? KeyPhrases.Where(p => p.Any(c => c > 0x374)).ToArray()
                : KeyPhrases.Where(p => p.All(c => c < 0x374)).ToArray();

            if (pool.Length == 0)
                pool = KeyPhrases;

            int wanted = 1 + _random.Next(3);
            return pool.OrderBy(_ => _random.Next()).Take(Math.Min(wanted, pool.Length)).ToList();
        }

        private static void TrackNewest(Dictionary<int, DateTime> newest, int userId, DateTime candidate)
        {
            if (!newest.TryGetValue(userId, out var current) || candidate > current)
                newest[userId] = candidate;
        }

        #endregion

        #region Watermarks and import log

        /// <summary>
        /// Writes the per-user import state. Without it the tables look like data that arrived from nowhere,
        /// and anything reading the watermarks (or a re-run of the real importer) sees every user as new.
        /// </summary>
        private void WriteWatermarks(Dictionary<int, DateTime> newestInteractionByUser, List<int> userIds, DateTime windowEnd)
        {
            using (var db = new AnalyticsEntitiesContext(_connectionString, true, false))
            {
                db.Configuration.AutoDetectChangesEnabled = false;
                try
                {
                    var existing = db.CopilotInteractionUserWatermarks
                        .Where(w => userIds.Contains(w.UserId))
                        .ToDictionary(w => w.UserId, w => w);

                    foreach (var userId in userIds)
                    {
                        bool hasData = newestInteractionByUser.TryGetValue(userId, out var newest);

                        if (!existing.TryGetValue(userId, out var watermark))
                        {
                            watermark = new CopilotInteractionUserWatermark { UserId = userId };
                            db.CopilotInteractionUserWatermarks.Add(watermark);
                        }

                        watermark.LastRunUtc = windowEnd;
                        watermark.LastError = null;

                        if (hasData)
                        {
                            watermark.LastInteractionUtc = newest;
                            watermark.ConsecutiveEmptyOrFailed = 0;
                            watermark.SkipUntilUtc = null;
                        }
                        else
                        {
                            // A user with no history looks exactly like an unlicensed one to the importer,
                            // so give them the same back-off state the real thing would.
                            watermark.ConsecutiveEmptyOrFailed = 2;
                            watermark.SkipUntilUtc = windowEnd.AddHours(72);
                        }
                    }

                    db.ChangeTracker.DetectChanges();
                    db.SaveChanges();
                }
                finally
                {
                    db.Configuration.AutoDetectChangesEnabled = true;
                }
            }
        }

        private void WriteImportLog(DateTime runStarted, int usersInScope, int usersWithData, GenerationStats stats)
        {
            using (var db = new AnalyticsEntitiesContext(_connectionString, true, false))
            {
                db.CopilotInteractionImportLogs.Add(new CopilotInteractionImportLog
                {
                    RunStartedUtc = runStarted,
                    RunFinishedUtc = DateTime.UtcNow,
                    UsersInScope = usersInScope,
                    UsersScanned = usersWithData,
                    UsersSkipped = Math.Max(0, usersInScope - usersWithData),
                    UsersFailed = 0,
                    InteractionsRead = stats.Interactions,
                    InteractionsSaved = stats.Interactions,
                    CognitiveDocsScored = stats.ScoredPrompts,
                    Error = null
                });

                db.SaveChanges();
            }
        }

        #endregion

        private string WeightedPick((string Name, int Weight)[] options)
        {
            int total = options.Sum(o => o.Weight);
            int roll = _random.Next(total);

            foreach (var option in options)
            {
                roll -= option.Weight;
                if (roll < 0)
                    return option.Name;
            }

            return options[options.Length - 1].Name;
        }

        private class PlannedSession
        {
            public int UserId { get; set; }
            public string SessionRef { get; set; }
        }

        private class Lookups
        {
            public Dictionary<string, int> InteractionTypes { get; set; }
            public Dictionary<string, int> AppClasses { get; set; }
            public Dictionary<string, int> ConversationTypes { get; set; }
            public Dictionary<string, int> Locales { get; set; }
            public Dictionary<string, int> Devices { get; set; }
            public Dictionary<string, int> Languages { get; set; }
            public Dictionary<string, int> KeyPhrases { get; set; }
        }

        private class GenerationStats
        {
            public int Sessions;
            public int Interactions;
            public int Prompts;
            public int Responses;
            public int ScoredPrompts;
            public int KeyPhraseLinks;
        }
    }
}
