using Common.Entities;
using Common.Entities.CopilotAdoption;
using Common.Entities.Entities;
using Common.Entities.Entities.AuditLog;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Tests.FakeDataGen.Copilot
{
    /// <summary>
    /// What the scenario planted, for the console summary and for tests.
    /// </summary>
    public class CopilotAdoptionScenarioResult
    {
        public int LicensedUsersShaped { get; set; }

        public int UnlicensedUsersShaped { get; set; }

        public int InteractionsCreated { get; set; }

        public int AgentsCreated { get; set; }

        public int DisabledSeats { get; set; }

        /// <summary>Users per band the personas are built to produce.</summary>
        public Dictionary<string, int> UsersByExpectedBand { get; } = new Dictionary<string, int>();

        public Dictionary<string, int> UsersByPersona { get; } = new Dictionary<string, int>();
    }

    /// <summary>
    /// Shapes Copilot activity into a tenant that is actually worth looking at in the Copilot Adoption
    /// tool: users spread across every stage of the funnel, several distinctly different engagement
    /// shapes at similar scores, departments that differ from one another, seats worth reclaiming, an
    /// unlicensed population making a licence case for itself, and an agent inventory containing all
    /// four health verdicts.
    ///
    /// This exists because scattering N interactions over N users at random - which is what the plain
    /// generator does, and is the right thing for importer and volume testing - produces a tenant where
    /// every licensed user has almost exactly the same number of interactions on almost exactly the
    /// same number of days. That lands the entire population in one band, leaves the funnel, the
    /// treemap, the profile radar and every "who should I target" panel showing nothing, and makes the
    /// tool look broken when it is working perfectly.
    ///
    /// The targets are expressed in the three signals the analysis measures - distinct active dates,
    /// interactions per active date, and distinct <c>app_host</c> values - so the bands are produced by
    /// the real scoring code rather than asserted here. See <see cref="CopilotAdoptionPersonas"/>.
    /// </summary>
    public class CopilotAdoptionScenarioGenerator
    {
        private readonly Random _random;
        private readonly CopilotChatFactory _chats;

        public CopilotAdoptionScenarioGenerator(Random random, CopilotChatFactory chats)
        {
            _random = random;
            _chats = chats;
        }

        /// <param name="users">The population to shape. Licensed/unlicensed is resolved from the database.</param>
        /// <param name="windowEndUtc">"Now" for the scenario - the end of the reporting window.</param>
        /// <param name="windowDays">The analysis reporting window, in days. Matches CopilotAdoptionOptions.WindowDays.</param>
        /// <param name="historyDaysAvailable">How far back the caller wanted activity spread.</param>
        /// <param name="unlicensedActivePercent">Share of unlicensed users given Copilot activity, making a licence case.</param>
        public CopilotAdoptionScenarioResult Generate(
            AnalyticsEntitiesContext db,
            IReadOnlyList<User> users,
            EventOperation operation,
            DateTime windowEndUtc,
            int windowDays = 28,
            int historyDaysAvailable = 90,
            int unlicensedActivePercent = 12)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));
            if (users == null) throw new ArgumentNullException(nameof(users));
            if (operation == null) throw new ArgumentNullException(nameof(operation));

            var result = new CopilotAdoptionScenarioResult();
            var seatLicenceIds = ResolveSeatLicenceTypeIds(db);

            if (seatLicenceIds.Count == 0)
            {
                Console.WriteLine("  ! No Copilot seat licence type found - skipping adoption scenario.");
                return result;
            }

            var userIds = users.Select(u => u.ID).ToList();
            var licensedUserIds = new HashSet<int>(
                db.UserLicenseTypeLookups
                    .Where(l => seatLicenceIds.Contains(l.LicenseTypeId) && userIds.Contains(l.UserId))
                    .Select(l => l.UserId)
                    .ToList());

            var licensed = users.Where(u => licensedUserIds.Contains(u.ID)).ToList();
            var unlicensed = users.Where(u => !licensedUserIds.Contains(u.ID)).ToList();

            if (licensed.Count == 0)
            {
                Console.WriteLine("  ! No users hold a Copilot seat - skipping adoption scenario.");
                return result;
            }

            Console.WriteLine($"  Shaping {licensed.Count:N0} licensed and {unlicensed.Count:N0} unlicensed users into adoption personas...");
            VerifyPersonaCatalogue(windowEndUtc, windowDays);

            var previousAutoDetect = db.Configuration.AutoDetectChangesEnabled;
            db.Configuration.AutoDetectChangesEnabled = false;
            try
            {
                var maturityByDepartment = AssignDepartmentMaturity(licensed);

                foreach (var user in licensed)
                {
                    var maturity = maturityByDepartment[DepartmentKey(user)];
                    var persona = CopilotAdoptionPersonas.Pick(CopilotAdoptionPersonas.MixFor(maturity), _random);

                    ApplyPersona(db, user, operation, persona, windowEndUtc, windowDays, historyDaysAvailable, result);

                    Increment(result.UsersByPersona, persona.Name);
                    Increment(result.UsersByExpectedBand, CopilotAdoptionScoring.BandDisplayName(persona.ExpectedBand));
                    result.LicensedUsersShaped++;

                    if (result.LicensedUsersShaped % 25 == 0)
                    {
                        db.SaveChanges();
                    }
                }

                db.SaveChanges();

                result.UnlicensedUsersShaped = ShapeUnlicensedPopulation(
                    db, unlicensed, operation, windowEndUtc, windowDays, unlicensedActivePercent, result);

                db.SaveChanges();

                result.AgentsCreated = ShapeAgentInventory(db, licensed, operation, windowEndUtc, result);
                db.SaveChanges();
            }
            finally
            {
                db.Configuration.AutoDetectChangesEnabled = previousAutoDetect;
            }

            return result;
        }

        /// <summary>
        /// Uses the product's own licence classifier rather than matching a SKU string here, so the
        /// scenario always agrees with what the tool will actually count as a seat.
        /// </summary>
        private static List<int> ResolveSeatLicenceTypeIds(AnalyticsEntitiesContext db)
        {
            var licenceTypes = db.LicenseTypes
                .Select(l => new { l.ID, l.Name, l.SKUID })
                .ToList()
                .Select(l => new LicenceTypeRow { Id = l.ID, Name = l.Name, SkuPartNumber = l.SKUID })
                .ToList();

            return CopilotLicenceClassifier.ResolveSeatLicenceTypeIds(licenceTypes, null);
        }

        /// <summary>
        /// Spreads maturity tiers across the departments that actually hold seats, so the department
        /// charts show contrast. Deliberately deterministic in ordering (the department list is sorted)
        /// so two runs against the same population produce a comparable shape.
        /// </summary>
        private static Dictionary<string, DepartmentMaturity> AssignDepartmentMaturity(IReadOnlyList<User> licensed)
        {
            var departments = licensed
                .Select(DepartmentKey)
                .Distinct()
                .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var map = new Dictionary<string, DepartmentMaturity>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < departments.Count; i++)
            {
                // Round-robin guarantees all three tiers appear even with only three departments.
                map[departments[i]] = (DepartmentMaturity)(i % 3);
            }
            return map;
        }

        private static string DepartmentKey(User user)
        {
            return user.DepartmentId.HasValue
                ? user.DepartmentId.Value.ToString()
                : "(none)";
        }

        /// <summary>
        /// Runs every persona through the product's own scoring code and warns if any of them no longer
        /// lands in the band it claims.
        ///
        /// The persona targets are derived from the shipped tuning defaults. If someone changes the
        /// frequency target, a weight or a band threshold, this catalogue silently starts generating a
        /// tenant that does not demonstrate what it says it does - and the failure is invisible, because
        /// the data still looks plausible. Checking it against the real scorer at run time makes that
        /// loud instead.
        /// </summary>
        private static void VerifyPersonaCatalogue(DateTime windowEndUtc, int windowDays)
        {
            var windowStartUtc = windowEndUtc.Date.AddDays(-windowDays);
            var mismatches = new List<string>();

            foreach (var persona in CopilotAdoptionPersonas.All)
            {
                var row = new LicensedUserUsageRow
                {
                    UserId = 0,
                    Interactions = persona.WindowInteractions,
                    ActiveDays = persona.ActiveDaysInWindow,
                    AppsUsed = persona.DistinctApps,
                    PriorInteractions = persona.PriorInteractions,
                    FirstInteractionUtc = persona.PriorInteractions > 0
                        ? (DateTime?)windowStartUtc.AddDays(-persona.PriorDaysAgo)
                        : (persona.ActiveDaysInWindow > 0 ? (DateTime?)windowStartUtc.AddDays(1) : null),
                    LastInteractionUtc = persona.ActiveDaysInWindow > 0
                        ? (DateTime?)windowEndUtc
                        : (persona.PriorInteractions > 0 ? (DateTime?)windowStartUtc.AddDays(-1) : null),
                };

                var scored = CopilotAdoptionScoring.Score(row, windowStartUtc, windowEndUtc, auditAvailable: true);

                if (scored.Band != persona.ExpectedBand)
                {
                    mismatches.Add(
                        $"    {persona.Name}: expected {CopilotAdoptionScoring.BandDisplayName(persona.ExpectedBand)}, " +
                        $"scoring gives {scored.BandName} ({scored.AdoptionScore:F1})");
                }
                else if (Math.Abs(scored.AdoptionScore - persona.ExpectedScore) > 0.5)
                {
                    mismatches.Add(
                        $"    {persona.Name}: expected score {persona.ExpectedScore:F1}, scoring gives {scored.AdoptionScore:F1}");
                }
            }

            if (mismatches.Count == 0)
            {
                Console.WriteLine($"  Persona catalogue verified against the live scoring rules ({CopilotAdoptionPersonas.All.Count} personas).");
                return;
            }

            Console.WriteLine("  ! Persona catalogue no longer matches the scoring rules - the tuning defaults have moved:");
            foreach (var mismatch in mismatches)
            {
                Console.WriteLine(mismatch);
            }
            Console.WriteLine("  ! Generation will continue, but the funnel will not come out as described.");
        }

        /// <summary>Generates the interactions that make one user land in their persona's band.</summary>
        private void ApplyPersona(
            AnalyticsEntitiesContext db,
            User user,
            EventOperation operation,
            AdoptionPersona persona,
            DateTime windowEndUtc,
            int windowDays,
            int historyDaysAvailable,
            CopilotAdoptionScenarioResult result)
        {
            if (!persona.AccountEnabled && user.AccountEnabled != false)
            {
                user.AccountEnabled = false;
                result.DisabledSeats++;
            }

            // Activity inside the reporting window - this is what the score is built from.
            if (persona.ActiveDaysInWindow > 0)
            {
                var apps = AppSetFor(user.ID, persona.DistinctApps);
                var dayOffsets = PickDistinctDayOffsets(persona.ActiveDaysInWindow, windowDays);
                var perDay = SpreadEvenly(persona.WindowInteractions, dayOffsets.Count);

                int appIndex = 0;
                for (int d = 0; d < dayOffsets.Count; d++)
                {
                    for (int n = 0; n < perDay[d]; n++)
                    {
                        // Rotating rather than random so every app in the set is certainly used -
                        // AppsUsed is a COUNT(DISTINCT app_host), so a random draw could quietly
                        // give a "broad" persona fewer apps than its band depends on.
                        var appHost = apps[appIndex++ % apps.Count];
                        var agent = persona.UsesAgents && _random.Next(100) < 35
                            ? _chats.GetOrCreateAgent(db, _random.Next(100) < 40)
                            : null;

                        _chats.Create(db, user, operation,
                            TimestampOn(windowEndUtc, dayOffsets[d]), appHost, agent, withMetadata: false);
                        result.InteractionsCreated++;
                    }
                }
            }

            // Activity before the window - the only thing separating "used it and stopped" from
            // "never started", which are the two populations with completely different actions.
            if (persona.PriorInteractions > 0)
            {
                var priorDaysAgo = ClampPriorDaysAgo(persona.PriorDaysAgo, windowDays, historyDaysAvailable);
                var apps = AppSetFor(user.ID, Math.Max(1, persona.DistinctApps));

                for (int i = 0; i < persona.PriorInteractions; i++)
                {
                    // Spread over a fortnight around the nominal lapse point, so "days since last use"
                    // is not identical for every dormant user.
                    var offset = priorDaysAgo + _random.Next(0, 14);
                    _chats.Create(db, user, operation,
                        TimestampOn(windowEndUtc, offset), apps[i % apps.Count], null, withMetadata: false);
                    result.InteractionsCreated++;
                }
            }
        }

        /// <summary>
        /// Gives a slice of the unlicensed population real Copilot usage, which is what the licence
        /// opportunity list is built from: people doing the work without holding a seat.
        /// </summary>
        private int ShapeUnlicensedPopulation(
            AnalyticsEntitiesContext db,
            IReadOnlyList<User> unlicensed,
            EventOperation operation,
            DateTime windowEndUtc,
            int windowDays,
            int unlicensedActivePercent,
            CopilotAdoptionScenarioResult result)
        {
            if (unlicensed.Count == 0 || unlicensedActivePercent <= 0) return 0;

            int shaped = 0;
            foreach (var user in unlicensed)
            {
                if (_random.Next(100) >= unlicensedActivePercent) continue;

                // A spread of strengths, so the opportunity ranking has something to rank: a couple of
                // obvious candidates, a long tail of occasional users.
                var activeDays = _random.Next(1, 11);
                var perDay = _random.Next(1, 5);
                var apps = AppSetFor(user.ID, _random.Next(1, 4));
                var dayOffsets = PickDistinctDayOffsets(activeDays, windowDays);

                int appIndex = 0;
                foreach (var offset in dayOffsets)
                {
                    for (int n = 0; n < perDay; n++)
                    {
                        var agent = _random.Next(100) < 20 ? _chats.GetOrCreateAgent(db, true) : null;
                        _chats.Create(db, user, operation,
                            TimestampOn(windowEndUtc, offset), apps[appIndex++ % apps.Count], agent, withMetadata: false);
                        result.InteractionsCreated++;
                    }
                }

                shaped++;
                if (shaped % 25 == 0) db.SaveChanges();
            }

            return shaped;
        }

        /// <summary>
        /// Plants one agent for each health verdict. Without this every generated agent is used
        /// continuously right up to "now", so the inventory only ever shows Keep and the
        /// Review/Retire/New logic is invisible.
        ///
        /// The day offsets deliberately reach back further than the caller's requested spread: a Retire
        /// verdict needs an agent idle for at least <c>AgentRetireInactiveDays</c> (90) that is still
        /// inside the inventory's own <c>AgentHistoryDays</c> (120) horizon, so a default 90-day run
        /// could otherwise never produce a retirement candidate.
        /// </summary>
        private int ShapeAgentInventory(
            AnalyticsEntitiesContext db,
            IReadOnlyList<User> licensed,
            EventOperation operation,
            DateTime windowEndUtc,
            CopilotAdoptionScenarioResult result)
        {
            // (display name, first use days ago, last use days ago, distinct users)
            var archetypes = new[]
            {
                Tuple.Create("Contoso HR Assistant", 110, 1, 8),        // healthy: used right up to now
                Tuple.Create("Contoso Sales Coach", 110, 45, 5),        // Review: quiet for over a month
                Tuple.Create("Contoso Expenses Bot", 115, 100, 3),      // Retire: quiet for over 90 days
                Tuple.Create("Contoso Onboarding Guide", 12, 1, 4),     // New: too young to judge
            };

            int created = 0;
            foreach (var archetype in archetypes)
            {
                var name = archetype.Item1;
                var firstUseDaysAgo = archetype.Item2;
                var lastUseDaysAgo = archetype.Item3;
                var userCount = Math.Min(archetype.Item4, licensed.Count);

                var agent = _chats.GetOrCreateNamedAgent(
                    db, name, $"Copilot.Studio.Default-{name.Replace(" ", "")}", true);
                created++;

                var participants = PickDistinct(licensed, userCount);

                // Pin the two ends explicitly so the first/last-use dates - which are the whole basis
                // of the health verdict - are exactly what this archetype claims.
                CreateAgentInteraction(db, participants[0], operation, agent, windowEndUtc, firstUseDaysAgo, result);
                CreateAgentInteraction(db, participants[0], operation, agent, windowEndUtc, lastUseDaysAgo, result);

                foreach (var user in participants)
                {
                    var burst = _random.Next(2, 8);
                    for (int i = 0; i < burst; i++)
                    {
                        var offset = _random.Next(lastUseDaysAgo, firstUseDaysAgo + 1);
                        CreateAgentInteraction(db, user, operation, agent, windowEndUtc, offset, result);
                    }
                }

                db.SaveChanges();
            }

            return created;
        }

        private void CreateAgentInteraction(
            AnalyticsEntitiesContext db,
            User user,
            EventOperation operation,
            CopilotAgent agent,
            DateTime windowEndUtc,
            int daysAgo,
            CopilotAdoptionScenarioResult result)
        {
            _chats.Create(db, user, operation, TimestampOn(windowEndUtc, daysAgo),
                _chats.RandomAppHost(), agent, withMetadata: false);
            result.InteractionsCreated++;
        }

        /// <summary>
        /// A per-user slice of the app-host list. Rotating the start point by user id means different
        /// users favour different surfaces, so the per-app breakdown is not a flat bar.
        /// </summary>
        private static IReadOnlyList<string> AppSetFor(int userId, int wanted)
        {
            var all = CopilotActivityGeneratorConfig.AppHosts;
            var count = Math.Max(1, Math.Min(wanted, all.Length));
            var start = Math.Abs(userId) % all.Length;

            var picked = new List<string>(count);
            for (int i = 0; i < count; i++)
            {
                picked.Add(all[(start + i) % all.Length]);
            }
            return picked;
        }

        /// <summary>
        /// Distinct whole-day offsets inside the reporting window. Two days of headroom are left at the
        /// far edge: the analysis window is relative to when the *report* is run, so a scenario built
        /// today and viewed in two days' time would otherwise lose its earliest active days and quietly
        /// drop users a band.
        /// </summary>
        private List<int> PickDistinctDayOffsets(int wanted, int windowDays)
        {
            var usable = Math.Max(1, windowDays - 2);
            var count = Math.Min(wanted, usable);

            var offsets = Enumerable.Range(0, usable).ToList();
            // Partial Fisher-Yates: only the first `count` positions need to be settled.
            for (int i = 0; i < count; i++)
            {
                var j = _random.Next(i, offsets.Count);
                var tmp = offsets[i];
                offsets[i] = offsets[j];
                offsets[j] = tmp;
            }
            return offsets.Take(count).ToList();
        }

        /// <summary>
        /// Splits a total as evenly as possible across the active days, so interactions-per-active-day
        /// - the depth component - comes out at the intended figure instead of being dominated by one
        /// busy day.
        /// </summary>
        private static List<int> SpreadEvenly(int total, int buckets)
        {
            var result = new List<int>(buckets);
            if (buckets <= 0) return result;

            var baseCount = total / buckets;
            var remainder = total % buckets;
            for (int i = 0; i < buckets; i++)
            {
                result.Add(baseCount + (i < remainder ? 1 : 0));
            }
            return result;
        }

        /// <summary>
        /// Keeps a dormant user's last activity outside the reporting window but inside the history the
        /// caller asked for - otherwise a short run would place it beyond the queried history and the
        /// user would report as "never used" instead of "dormant".
        /// </summary>
        private static int ClampPriorDaysAgo(int wanted, int windowDays, int historyDaysAvailable)
        {
            var latest = windowDays + 5;
            var earliest = Math.Max(latest, historyDaysAvailable - 5);
            return Math.Min(Math.Max(wanted, latest), earliest);
        }

        /// <summary>A timestamp during business hours on the day <paramref name="daysAgo"/> before the window end.</summary>
        private DateTime TimestampOn(DateTime windowEndUtc, int daysAgo)
        {
            var day = windowEndUtc.Date.AddDays(-daysAgo);
            var stamp = day.AddHours(8 + _random.Next(0, 10)).AddMinutes(_random.Next(0, 60));

            // Never place an interaction in the future - it reads as corrupt data in the UI.
            return stamp > windowEndUtc ? windowEndUtc.AddMinutes(-_random.Next(1, 240)) : stamp;
        }

        private List<User> PickDistinct(IReadOnlyList<User> pool, int count)
        {
            var picked = new List<User>();
            var used = new HashSet<int>();
            var attempts = 0;

            while (picked.Count < count && attempts < count * 20)
            {
                attempts++;
                var candidate = pool[_random.Next(pool.Count)];
                if (used.Add(candidate.ID)) picked.Add(candidate);
            }

            if (picked.Count == 0) picked.Add(pool[0]);
            return picked;
        }

        private static void Increment(Dictionary<string, int> counts, string key)
        {
            int existing;
            counts.TryGetValue(key, out existing);
            counts[key] = existing + 1;
        }
    }
}
