using Common.Entities.CopilotAdoption;
using Common.Entities.CopilotCosts;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Tests.FakeDataGen.Demo
{
    /// <summary>
    /// Synthetic Copilot credit history, written to the preview collector's own tables exactly as the collector
    /// leaves them: one sweep a day of every directory object, each person's latest reading per service, the
    /// spending policies and tenant balances each sweep read, and the archived publication behind every reading.
    /// </summary>
    /// <remarks>
    /// <para><b>Balances, exactly as the source reports them.</b> A reading is what the source says a person has
    /// consumed in its current period, which here resets on the first of each calendar month. The rows keep the
    /// live collector's shape: <c>source_month</c> is NULL and <c>period_status</c> is <c>unverified</c>, so a report
    /// cannot assume the accounting period any more than it could on a real tenant. Successive readings are
    /// snapshots of a running balance and must never be added together.</para>
    /// <para><b>Driven by the demo's own Copilot activity.</b> Credits accrue on the days a person's timeline has
    /// Copilot agent or Cowork turns, so spend lines up with the activity the adoption report shows for the same
    /// person. Deliberate counter-examples keep the story honest: a few lightly-evidenced seat holders run a
    /// scheduled agent that spends heavily on days they never open Copilot; some champions are heavy and
    /// well-evidenced agent users; some people without a seat pay for metered agents; disabled accounts are
    /// refused. More spend is not more evidence of value, and the data is built so a report can show that.</para>
    /// <para><b>Imperfect, like a real sweep.</b> A couple of days have no sweep at all (absent, not zero), a
    /// throttled day misses part of the directory, a degraded day leaves some readings partial with an
    /// unevaluated category, a few readings are corrected later the same day (both publications are archived and
    /// the current tables keep the complete one), some readings lag a day, a newly enabled service reports a unit
    /// this build does not know, and a handful of directory accounts the user import never picked up carry
    /// balances that match no imported person.</para>
    /// <para>Service ids, policy names, limits and the tenant pool are illustrative synthetic values, not
    /// Microsoft's catalogue or prices. Bounded: at most <see cref="MaxHistoryDays"/> days, and fewer for large
    /// populations so the observation count stays near <see cref="ObservationBudget"/>.</para>
    /// </remarks>
    internal sealed class DemoCopilotCredits
    {
        internal const int MaxHistoryDays = 92;
        internal const int MinHistoryDays = 7;
        internal const long ObservationBudget = 1500000;
        internal const string CreditUnit = "copilotCredit";
        internal const string FutureUnit = "unknownFutureValue";

        internal const string ChatAgents = "copilotChatAgents";
        internal const string Cowork = "coworkAgent";
        internal const string SharePointAgents = "sharePointAgents";

        /// <summary>A service the pilot policy enabled recently, whose unit this build's contract does not know.</summary>
        internal const string PreviewService = "previewAgentService";
        internal const int PreviewServiceDays = 20;

        internal const string PolicyAllUsers = "contoso-demo-policy-all-users";
        internal const string PolicyPilot = "contoso-demo-policy-agents-pilot";
        internal const string PolicyPaused = "contoso-demo-policy-paused";

        /// <summary>Greek on purpose: a policy name is customer text and must survive the round trip.</summary>
        internal const string PausedPolicyName = "Contoso pilot \u2013 \u0395\u03bb\u03bb\u03ac\u03b4\u03b1 (paused)";

        internal const double AllUsersPerUserLimit = 1200;
        internal const double AllUsersPeriodLimit = 250000;
        internal const double PilotPeriodLimit = 600000;
        internal const double PrepaidPool = 150000;
        internal static readonly string[] PilotDepartments = { "Sales", "Customer Support" };

        private const int Chat = 0, CoworkIndex = 1, SharePoint = 2, Preview = 3, ServiceSlots = 4;
        private const string Limited = "#microsoft.graph.limitedCostManagementQuantity";
        private const string Unlimited = "#microsoft.graph.unlimitedCostManagementQuantity";
        private const string GraphRoot = "https://graph.microsoft.com/beta/copilot/costManagement/";

        internal enum Profile { Standard, AutomationHeavy, AgentPowerUser, MeteredHeavy }

        private readonly DemoOptions _options;
        private readonly IDemoSink _sink;
        private readonly int _historyDays;
        private readonly DateTime _firstSweep;
        private readonly bool[] _missing, _throttled, _degraded;
        private readonly int[] _affected;
        private readonly Guid[] _runs;
        private readonly string _generation;
        private readonly int _unresolved;
        private readonly double[] _tenantConsumed;

        public DemoCopilotCredits(DemoOptions options, IDemoSink sink)
        {
            _options = options;
            _sink = sink;
            _unresolved = Math.Max(2, options.Users / 125);
            var directory = (long)options.Users + _unresolved;
            var budgeted = (int)Math.Max(MinHistoryDays, Math.Min(MaxHistoryDays, ObservationBudget / directory));
            _historyDays = Math.Max(1, Math.Min(options.Days, budgeted));
            _firstSweep = options.AsOf.AddDays(-_historyDays);
            _generation = DemoRandom.Id(options.Seed, 2400, 0).ToString("N");
            _missing = new bool[_historyDays];
            _throttled = new bool[_historyDays];
            _degraded = new bool[_historyDays];
            _affected = new int[_historyDays];
            _runs = new Guid[_historyDays];
            _tenantConsumed = new double[_historyDays];
            for (int k = 0; k < _historyDays; k++)
            {
                _runs[k] = DemoRandom.Id(options.Seed, 2401, 0, k);
                // An importer outage now and again - never in the final days, so the latest snapshot is current.
                var tail = _historyDays - k;
                _missing[k] = tail > 5 && k > 2 && DemoRandom.Value(options.Seed, 0, k, 2402) % 29 == 0;
                _throttled[k] = !_missing[k] && tail > 4 && DemoRandom.Value(options.Seed, 0, k, 2403) % 23 == 0;
                _degraded[k] = !_missing[k] && !_throttled[k] && tail > 3 && DemoRandom.Value(options.Seed, 0, k, 2404) % 13 == 0;
                // One person is always affected on a throttled or degraded day, so its run is honestly incomplete.
                _affected[k] = 1 + (int)(DemoRandom.Value(options.Seed, 0, k, 2405) % (uint)options.Users);
            }
        }

        public int HistoryDays => _historyDays;
        public int UnresolvedObjects => _unresolved;

        private DateTime SweepDate(int k) => _firstSweep.AddDays(k);

        private static DateTime MonthStart(DateTime date) => new DateTime(date.Year, date.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        private static DateTimeOffset At(DateTime date, double hours) =>
            new DateTimeOffset(DateTime.SpecifyKind(date.Date, DateTimeKind.Utc).AddHours(hours));

        private static string Iso(DateTimeOffset value) => value.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

        private double Fraction(int user, int day, int salt) => (DemoRandom.Value(_options.Seed, user, day, salt) % 1000) / 1000d;

        private bool RunComplete(int k) => !_throttled[k] && !_degraded[k];

        /// <summary>The collector's control row and one run per sweep day. Written before any work row refers to a run.</summary>
        public void WriteRuns()
        {
            _sink.Write(DemoTables.CostControl, 1, _generation, false);
            var pages = Math.Max(1, (_options.Users + _unresolved + 998) / 999);
            for (int k = 0; k < _historyDays; k++)
            {
                if (_missing[k]) continue;
                var date = SweepDate(k);
                var complete = RunComplete(k);
                _sink.Write(DemoTables.CostRuns, _runs[k], _generation, date, true, pages, (long)(4 + k % 5),
                    At(date, 12.5), At(date, 12.5), complete ? (object)At(date, 12.75) : null,
                    complete ? "complete" : _throttled[k] ? "throttled" : "partial", 2);
            }
        }

        /// <summary>The investment story one person's spend follows. Decided from stable ids, so a rerun is identical.</summary>
        internal Profile ProfileFor(DemoUser user)
        {
            if (!user.Profile.AccountEnabled) return Profile.Standard;
            var draw = DemoRandom.Value(_options.Seed, user.Id, 0, 2406) % 100;
            if (user.CopilotLicensed)
            {
                var band = user.Persona.ExpectedBand;
                if ((band == AdoptionBand.Trialling || band == AdoptionBand.Developing) && draw < 9) return Profile.AutomationHeavy;
                if (band == AdoptionBand.Dormant && draw < 6) return Profile.AutomationHeavy;
                if (band == AdoptionBand.Champion && draw < 30) return Profile.AgentPowerUser;
                return Profile.Standard;
            }
            return user.UnlicensedDemand && draw < 14 ? Profile.MeteredHeavy : Profile.Standard;
        }

        internal static bool InPilot(DemoUser user) => PilotDepartments.Contains(user.Profile.Department);

        /// <summary>Credits a person used on one timeline day, per service slot. Nothing for a disabled account.</summary>
        internal double[] DailyCredits(DemoUser user, DemoTimeline timeline, int d, Profile profile)
        {
            var credits = new double[ServiceSlots];
            if (!user.Profile.AccountEnabled || d < -DemoTimeline.WarmupDays || d >= _options.Days) return credits;
            var day = timeline.Day(d);
            var age = _options.Days - d;
            var working = DemoCalendar.IsWorkingDate(_options.Start.AddDays(d));
            var pilot = InPilot(user);

            // A scheduled agent spends on every working day once it is set up, whether or not its owner opens Copilot.
            if (profile == Profile.AutomationHeavy && working && age <= 40 + user.Id % 30)
                credits[CoworkIndex] += 50 + 45 * Fraction(user.Id, d, 2430);
            if (profile == Profile.MeteredHeavy && working && day.CopilotTurns == 0 && age <= 45
                && DemoRandom.Value(_options.Seed, user.Id, d, 2431) % 100 < 30)
                credits[Chat] += 10 + 15 * Fraction(user.Id, d, 2432);

            for (int slot = 0; slot < day.CopilotTurns; slot++)
            {
                var jitter = Fraction(user.Id, d, 2410 + slot);
                if (!user.CopilotLicensed)
                {
                    // Without a seat, agent turns in Copilot Chat are metered.
                    if (profile == Profile.MeteredHeavy) credits[Chat] += 28 + 20 * jitter;
                    else if (DemoRandom.Value(_options.Seed, user.Id, d, 2420 + slot) % 100 < 45) credits[Chat] += 1.2 + 1.4 * jitter;
                    continue;
                }
                var host = timeline.HostIndex(d, slot);
                var agent = timeline.Agent(d, slot);
                if (host == 7 || agent == 5) credits[CoworkIndex] += (7.5 + 4.5 * jitter) * (profile == Profile.AgentPowerUser ? 1.5 : 1);
                else if (agent == 2) credits[Chat] += 1.6 + 0.8 * jitter;
                else if (agent == 4) credits[Chat] += 0.4 + 0.2 * jitter;
                else if (agent == 1 && pilot) credits[SharePoint] += 2.2 + 1.1 * jitter;
                if (profile == Profile.AgentPowerUser && agent >= 1 && agent <= 4) credits[Chat] += 12 + 8 * jitter;
                if (pilot && agent > 0 && age <= PreviewServiceDays) credits[Preview] += 0.5 + jitter;
            }
            return credits;
        }

        private List<(string Id, int Slot)> ServicesFor(bool pilot, DateTime date)
        {
            var services = new List<(string, int)> { (ChatAgents, Chat), (Cowork, CoworkIndex) };
            if (pilot)
            {
                services.Add((SharePointAgents, SharePoint));
                if (date >= _options.AsOf.AddDays(-PreviewServiceDays)) services.Add((PreviewService, Preview));
            }
            return services;
        }

        /// <summary>One person's sweeps: an observation per sweep day with its service readings, the archive and the work row.</summary>
        public void WriteUser(DemoUser user, DemoTimeline timeline)
        {
            var objectId = Guid.Parse(DemoPopulation.AzureAdObjectId(_options.Seed, user.Id));
            var profile = ProfileFor(user);
            var pilot = InPilot(user);
            var entitled = user.Profile.AccountEnabled;
            var firstDay = Math.Max(-DemoTimeline.WarmupDays, (MonthStart(_firstSweep) - _options.Start).Days);
            var daily = new double[Math.Max(0, _options.Days - firstDay)][];
            for (int d = firstDay; d < _options.Days; d++) daily[d - firstDay] = DailyCredits(user, timeline, d, profile);

            for (int k = 0; k < _historyDays; k++)
            {
                if (_missing[k]) continue;
                var date = SweepDate(k);
                var run = _runs[k];
                if (_throttled[k] && (user.Id == _affected[k] || DemoRandom.Value(_options.Seed, user.Id, k, 2440) % 100 < 18))
                {
                    _sink.Write(DemoTables.CostWork, run, objectId, user.Id, 1, At(date, 13.5), false, false, false, false, false, 0, "throttled");
                    continue;
                }

                var observedAt = At(date, 6 + (user.Id % 180) / 60d);
                var lagging = DemoRandom.Value(_options.Seed, user.Id, k, 2441) % 33 == 0;
                var through = date.AddDays(lagging ? -2 : -1);
                var modified = At(through.AddDays(1), 2 + (user.Id % 120) / 60d);
                var services = ServicesFor(pilot, date);
                var readings = Readings(services, daily, firstDay, MonthStart(date), through, modified, pilot, entitled, lagging);

                var persistentPartial = _degraded[k] && readings.Count > 1
                    && (user.Id == _affected[k] || DemoRandom.Value(_options.Seed, user.Id, k, 2442) % 100 < 3);
                var corrected = !persistentPartial && readings.Count > 1 && DemoRandom.Value(_options.Seed, user.Id, k, 2443) % 1000 < 8;
                var unevaluated = new List<JObject>();
                if (persistentPartial)
                {
                    var failed = (int)(DemoRandom.Value(_options.Seed, user.Id, k, 2444) % (uint)readings.Count);
                    unevaluated.Add(Unevaluated(readings[failed].ServiceId, "upstreamFailure"));
                    readings.RemoveAt(failed);
                }
                var selection = Selection(date, observedAt);

                if (corrected)
                {
                    // The first publication missed a category; a retry three hours later read everything. Both
                    // are archived, and the current tables keep the complete one, as the live collector promotes it.
                    var early = Observation(user.Id, objectId, observedAt, Selection(date, observedAt), readings.Skip(1).ToList(),
                        new List<JObject> { Unevaluated(readings[0].ServiceId, "upstreamFailure") }, k, 1);
                    _sink.Write(DemoTables.CostHistory, run, early.PublicationId, date, objectId, observedAt, (byte)1, JsonConvert.SerializeObject(early));
                    observedAt = observedAt.AddHours(3);
                    selection = Selection(date, observedAt);
                }

                var quality = (byte)(unevaluated.Count == 0 ? 2 : 1);
                var observation = Observation(user.Id, objectId, observedAt, selection, readings, unevaluated, k, 0);
                WriteObservation(run, date, objectId, user.Id, observation, quality);
                if (entitled)
                    foreach (var reading in readings.Where(r => r.Unit == CreditUnit))
                        _tenantConsumed[k] += reading.ConsumedQuantity ?? 0;
                _sink.Write(DemoTables.CostWork, run, objectId, user.Id, corrected ? 2 : persistentPartial ? 3 : 1, observedAt.AddHours(1),
                    quality == 2, true, readings.Any(r => r.ConsumedQuantity.HasValue), false, quality == 1, unevaluated.Count,
                    quality == 2 ? "observed" : "partial");
            }
        }

        private List<CostServiceObservation> Readings(List<(string Id, int Slot)> services, double[][] daily, int firstDay,
            DateTime periodStart, DateTime through, DateTimeOffset modified, bool pilot, bool entitled, bool lagging)
        {
            var consumed = services.Select(s => entitled ? MonthToDate(daily, firstDay, periodStart, through, s.Slot) : 0).ToArray();
            var creditSlots = services.Select((s, i) => (s, i)).Where(x => x.s.Slot != Preview).Select(x => x.i).ToList();
            var policy = pilot ? PolicyPilot : PolicyAllUsers;
            var exhausted = false;
            double? remaining = null;
            if (!pilot)
            {
                // The per-user limit is one budget across the policy's services: once it is spent, every service refuses.
                var total = creditSlots.Sum(i => consumed[i]);
                if (total >= AllUsersPerUserLimit)
                {
                    foreach (var i in creditSlots) consumed[i] = total > 0 ? consumed[i] * AllUsersPerUserLimit / total : 0;
                    exhausted = true;
                }
                remaining = Math.Max(0, AllUsersPerUserLimit - creditSlots.Sum(i => consumed[i]));
            }

            var readings = new List<CostServiceObservation>();
            for (int i = 0; i < services.Count; i++)
            {
                var future = services[i].Slot == Preview;
                var unit = future ? FutureUnit : CreditUnit;
                CostQuantity left = future ? null : remaining.HasValue ? Quantity(Math.Round(remaining.Value, 3)) : UnlimitedQuantity();
                readings.Add(new CostServiceObservation
                {
                    ServiceId = services[i].Id,
                    SpendingPolicyId = policy,
                    ConsumedQuantity = Math.Round(consumed[i], 3),
                    Unit = unit,
                    ConfiguredPeriodLimit = Quantity(pilot ? PilotPeriodLimit : AllUsersPeriodLimit),
                    ConfiguredPerUserLimit = pilot ? UnlimitedQuantity() : Quantity(AllUsersPerUserLimit),
                    // A lagging reading arrives before the source has re-evaluated capacity: unknown, not zero.
                    EffectiveCapacityQuantity = lagging ? null : left,
                    RemainingQuantity = lagging ? null : left,
                    IsConsumptionAllowed = entitled && !exhausted,
                    AccessDecisionReason = !entitled ? "notEntitled" : exhausted ? "limitExhausted" : "allowed",
                    DataStatus = new JObject { ["lastModifiedDateTime"] = Iso(modified) },
                    Freshness = new CostDataFreshness { LastModifiedDateTime = modified },
                });
            }
            return readings;
        }

        /// <summary>Credits used in the current period up to <paramref name="through"/>; zero when the period started after it.</summary>
        private double MonthToDate(double[][] daily, int firstDay, DateTime periodStart, DateTime through, int slot)
        {
            double total = 0;
            for (var day = periodStart; day <= through; day = day.AddDays(1))
            {
                var index = (day - _options.Start).Days - firstDay;
                if (index >= 0 && index < daily.Length) total += daily[index][slot];
            }
            return total;
        }

        private void WriteObservation(Guid run, DateTime date, Guid objectId, int? userId, CostUserObservation observation, byte quality)
        {
            _sink.Write(DemoTables.CostObservations, date, objectId, userId, observation.ObservedAt, quality, "unverified",
                observation.SelectionDataStatus.ToString(Formatting.None));
            foreach (var reading in observation.Services)
                _sink.Write(DemoTables.CostServices, date, objectId, Hash(reading.ServiceId), reading.ServiceId, reading.SpendingPolicyId,
                    reading.ConsumedQuantity, reading.Unit, reading.IsConsumptionAllowed, reading.AccessDecisionReason,
                    reading.Freshness?.LastModifiedDateTime, Serialize(reading.ConfiguredPeriodLimit), Serialize(reading.ConfiguredPerUserLimit),
                    Serialize(reading.EffectiveCapacityQuantity), Serialize(reading.RemainingQuantity), Serialize(reading.DataStatus));
            for (int i = 0; i < observation.UnevaluatedServices.Count; i++)
                _sink.Write(DemoTables.CostUnevaluated, date, objectId, i,
                    (string)observation.UnevaluatedServices[i]["serviceId"], (string)observation.UnevaluatedServices[i]["reason"]);
            _sink.Write(DemoTables.CostHistory, run, observation.PublicationId, date, objectId, observation.ObservedAt, quality,
                JsonConvert.SerializeObject(observation));
        }

        /// <summary>
        /// Directory accounts the user import never picked up: shared and service accounts that run agents all period,
        /// and recent joiners who appear in the directory a few days before the end. Their balances match no imported
        /// person, which is exactly the gap a report has to disclose rather than drop.
        /// </summary>
        public void WriteUnresolved()
        {
            for (int i = 1; i <= _unresolved; i++)
            {
                var objectId = DemoRandom.Id(_options.Seed, 2470, i);
                var joiner = i % 3 == 0;
                var appears = joiner ? Math.Max(0, _historyDays - 3 - i % 10) : 0;
                var rate = joiner ? 1 + (DemoRandom.Value(_options.Seed, i, 0, 2471) % 400) / 100d
                    : 6 + (DemoRandom.Value(_options.Seed, i, 0, 2472) % 2400) / 100d;
                var appearDate = SweepDate(appears);
                var seen = false;
                for (int k = appears; k < _historyDays; k++)
                {
                    if (_missing[k]) continue;
                    var date = SweepDate(k);
                    var observedAt = At(date, 9.25);
                    var modified = At(date, 2.5);
                    var selection = Selection(date, observedAt);
                    CostUserObservation observation;
                    byte quality;
                    if (joiner && !seen)
                    {
                        // A brand-new account's group memberships have not reached the policy yet.
                        observation = Observation(-i, objectId, observedAt, selection, new List<CostServiceObservation>(),
                            new List<JObject> { Unevaluated(ChatAgents, "noApplicablePolicy") }, k, 0);
                        quality = 1;
                    }
                    else
                    {
                        var periodStart = MonthStart(date);
                        var from = periodStart > appearDate ? periodStart : appearDate;
                        double used = 0;
                        for (var day = from; day < date; day = day.AddDays(1))
                            if (DemoCalendar.IsWorkingDate(day)) used += rate;
                        var exhausted = used >= AllUsersPerUserLimit;
                        used = Math.Round(Math.Min(AllUsersPerUserLimit, used), 3);
                        var left = Quantity(Math.Round(AllUsersPerUserLimit - used, 3));
                        observation = Observation(-i, objectId, observedAt, selection, new List<CostServiceObservation>
                        {
                            new CostServiceObservation
                            {
                                ServiceId = ChatAgents, SpendingPolicyId = PolicyAllUsers, ConsumedQuantity = used, Unit = CreditUnit,
                                ConfiguredPeriodLimit = Quantity(AllUsersPeriodLimit), ConfiguredPerUserLimit = Quantity(AllUsersPerUserLimit),
                                EffectiveCapacityQuantity = left, RemainingQuantity = left,
                                IsConsumptionAllowed = !exhausted, AccessDecisionReason = exhausted ? "limitExhausted" : "allowed",
                                DataStatus = new JObject { ["lastModifiedDateTime"] = Iso(modified) },
                                Freshness = new CostDataFreshness { LastModifiedDateTime = modified },
                            },
                        }, new List<JObject>(), k, 0);
                        quality = 2;
                        _tenantConsumed[k] += used;
                    }
                    WriteObservation(_runs[k], date, objectId, null, observation, quality);
                    seen = true;
                    _sink.Write(DemoTables.CostWork, _runs[k], objectId, null, 1, observedAt.AddHours(1), quality == 2, true,
                        observation.Services.Count > 0, false, quality == 1, observation.UnevaluatedServices.Count, quality == 2 ? "observed" : "partial");
                }
            }
        }

        /// <summary>
        /// The policy, service, group and tenant documents each sweep read before its users. Written after the people,
        /// because the tenant balance is the whole tenant's consumption in the period.
        /// </summary>
        public void WriteContext()
        {
            var created = new DateTimeOffset(_firstSweep.AddDays(-40), TimeSpan.Zero);
            var policies = new List<CostPolicyContext>
            {
                new CostPolicyContext
                {
                    Id = PolicyAllUsers, Type = "#microsoft.graph.allUsersSpendingPolicy", DisplayName = "Contoso - all users (pay-as-you-go)",
                    Status = "enabled", BillingMethods = "payAsYouGo", CreatedAt = created, ModifiedAt = created.AddDays(12),
                    PeriodLimit = Quantity(AllUsersPeriodLimit), PerUserLimit = Quantity(AllUsersPerUserLimit),
                },
                new CostPolicyContext
                {
                    Id = PolicyPilot, Type = "#microsoft.graph.selectedGroupsSpendingPolicy", DisplayName = "Contoso - Sales and Support agents pilot",
                    Status = "enabled", BillingMethods = "prepaidCredits,payAsYouGo", CreatedAt = created.AddDays(3), ModifiedAt = created.AddDays(20),
                    PeriodLimit = Quantity(PilotPeriodLimit), PerUserLimit = UnlimitedQuantity(),
                },
                new CostPolicyContext
                {
                    Id = PolicyPaused, Type = "#microsoft.graph.selectedGroupsSpendingPolicy", DisplayName = PausedPolicyName,
                    Status = "disabled", BillingMethods = "payAsYouGo", CreatedAt = created.AddDays(5), ModifiedAt = created.AddDays(30),
                    PeriodLimit = Quantity(20000), PerUserLimit = Quantity(300),
                },
            };
            var policyDocument = JsonConvert.SerializeObject(policies);
            for (int k = 0; k < _historyDays; k++)
            {
                if (_missing[k]) continue;
                var date = SweepDate(k);
                var run = _runs[k];
                _sink.Write(DemoTables.CostContext, run, At(date, 5.5), "policies", GraphRoot + "spendingPolicies", policyDocument);
                var pilotServices = new List<(string, string)> { (ChatAgents, "enabled"), (Cowork, "enabled"), (SharePointAgents, "enabled") };
                if (date >= _options.AsOf.AddDays(-PreviewServiceDays)) pilotServices.Add((PreviewService, "enabled"));
                Services(run, date, PolicyAllUsers, new List<(string, string)> { (ChatAgents, "enabled"), (Cowork, "enabled"), (SharePointAgents, "disabled") });
                Services(run, date, PolicyPilot, pilotServices);
                Services(run, date, PolicyPaused, new List<(string, string)> { (ChatAgents, "enabled") });
                Groups(run, date, PolicyPilot, 2);
                Groups(run, date, PolicyPaused, 1);

                // Part of the period's consumption is drawn from a prepaid pool; the rest is billed as it is used.
                var consumed = _tenantConsumed[k];
                var prepaid = Math.Min(PrepaidPool, consumed * 0.55);
                var freshness = new CostDataFreshness { LastModifiedDateTime = At(date, 4.5) };
                var balances = new List<CostTenantBalance>
                {
                    new CostTenantBalance
                    {
                        BillingMethod = "prepaidCredits", PurchasedQuantity = PrepaidPool, ConsumedQuantity = Math.Round(prepaid, 3),
                        RemainingQuantity = Math.Round(PrepaidPool - prepaid, 3), Unit = CreditUnit, Freshness = freshness,
                    },
                    new CostTenantBalance
                    {
                        BillingMethod = "payAsYouGo", ConsumedQuantity = Math.Round(consumed - prepaid, 3), Unit = CreditUnit, Freshness = freshness,
                    },
                };
                _sink.Write(DemoTables.CostContext, run, At(date, 5.75), "tenant", GraphRoot + "getTenantCreditBalance", JsonConvert.SerializeObject(balances));
            }
        }

        private void Services(Guid run, DateTime date, string policy, List<(string Id, string State)> services)
        {
            var document = services.Select(s => new CostPolicyServiceContext
            {
                ServiceId = s.Id, State = s.State, ParentStatus = policy == PolicyPaused ? "disabled" : "enabled", ParentEtag = "W/\"contoso-demo-1\"",
            }).ToList();
            _sink.Write(DemoTables.CostContext, run, At(date, 5.6), "services", GraphRoot + "spendingPolicies/" + policy + "/serviceSettings",
                JsonConvert.SerializeObject(document));
        }

        private void Groups(Guid run, DateTime date, string policy, int count)
        {
            var groups = Enumerable.Range(1, count).Select(i => DemoRandom.Id(_options.Seed, 2407, policy.Length, i)).ToList();
            _sink.Write(DemoTables.CostContext, run, At(date, 5.65), "groups",
                GraphRoot + "spendingPolicies/" + policy + "/microsoft.graph.selectedGroupsSpendingPolicy/assignedGroups", JsonConvert.SerializeObject(groups));
        }

        private CostUserObservation Observation(int owner, Guid objectId, DateTimeOffset observedAt, JObject selection,
            List<CostServiceObservation> readings, List<JObject> unevaluated, int k, int publication) =>
            new CostUserObservation
            {
                PublicationId = DemoRandom.Id(_options.Seed, 2480, owner, k, publication),
                Id = objectId.ToString(),
                ObservedAt = observedAt,
                SelectionDataStatus = selection,
                SelectionFreshness = new CostDataFreshness { LastModifiedDateTime = observedAt },
                Services = readings,
                UnevaluatedServices = unevaluated,
            };

        private static JObject Selection(DateTime date, DateTimeOffset observedAt) => new JObject
        {
            ["evaluatedDateTime"] = Iso(observedAt),
            ["groupMembershipAsOfDateTime"] = Iso(At(date, 1)),
            ["policyConfigurationAsOfDateTime"] = Iso(At(date, 1.5)),
            ["usageAsOfDateTime"] = Iso(At(date, 3)),
        };

        private static JObject Unevaluated(string serviceId, string reason) => new JObject { ["serviceId"] = serviceId, ["reason"] = reason };

        private static CostQuantity Quantity(double value) => new CostQuantity { Type = Limited, Value = value, Unit = CreditUnit };

        private static CostQuantity UnlimitedQuantity() => new CostQuantity { Type = Unlimited };

        private static byte[] Hash(string category)
        {
            using (var sha = SHA256.Create()) return sha.ComputeHash(Encoding.UTF8.GetBytes(category));
        }

        private static string Serialize(object value) => value == null ? null : JsonConvert.SerializeObject(value);
    }
}
