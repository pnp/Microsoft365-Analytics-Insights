using Common.Entities.Entities.AgentCosts;
using System;
using System.Globalization;
using WebJob.Office365ActivityImporter.Engine.AgentCosts;

namespace Tests.FakeDataGen.Demo
{
    /// <summary>
    /// One billed slice of a synthetic Copilot Studio agent: the (feature, channel, model, tool, knowledge
    /// source) tuple Microsoft charges against, plus how much of the agent's traffic it accounts for.
    /// </summary>
    /// <remarks>
    /// The credit rates are <b>illustrative, not Microsoft's price list</b>. They only need to be plausible
    /// and stable so the report's relative figures make sense; quoting real rates in synthetic data would
    /// invite someone to read the demo as a price quote.
    /// </remarks>
    internal sealed class DemoBilledSlice
    {
        public string FeatureName { get; set; }
        public string ChannelId { get; set; }
        public string LlmModel { get; set; }
        public string ToolInvoked { get; set; }
        public string KnowledgeSources { get; set; }

        /// <summary>Share of the agent's daily turns that bill under this slice.</summary>
        public decimal TurnShare { get; set; }

        /// <summary>Credits charged per turn on this slice.</summary>
        public decimal CreditsPerTurn { get; set; }

        /// <summary>Share of the agent's daily users who reach this slice at all.</summary>
        public decimal UserShare { get; set; }

        /// <summary>
        /// Share of this slice's credits that were consumed but not charged. Zero means the API reported
        /// nothing, which is stored as NULL rather than 0 - "not reported" and "nothing was waived" are
        /// different answers, and the column is nullable precisely so they stay different.
        /// </summary>
        public decimal NonBilledShare { get; set; }
    }

    /// <summary>A synthetic Copilot Studio agent, as the Power Platform licensing API would report it.</summary>
    internal sealed class DemoBilledAgent
    {
        public string AgentId { get; set; }
        public string AgentName { get; set; }
        public string EnvironmentId { get; set; }
        public string EnvironmentName { get; set; }
        public DemoBilledSlice[] Slices { get; set; }

        /// <summary>Blended credits per turn across the agent's slices.</summary>
        public decimal CreditsPerTurn
        {
            get
            {
                decimal blended = 0m;
                foreach (var slice in Slices) blended += slice.TurnShare * slice.CreditsPerTurn;
                return blended;
            }
        }
    }

    /// <summary>One Azure meter that a synthetic agent workload bills against.</summary>
    internal sealed class DemoAzureMeter
    {
        public string ResourceGroup { get; set; }

        /// <summary>Provider path under the resource group, e.g. <c>Microsoft.Search/searchServices/name</c>.</summary>
        public string ResourcePath { get; set; }
        public string ServiceName { get; set; }
        public string MeterCategory { get; set; }
        public string MeterSubCategory { get; set; }
        public string MeterName { get; set; }

        /// <summary>Cost the resource incurs simply by existing, per day.</summary>
        public decimal CostPerDay { get; set; }

        /// <summary>Additional cost per agent turn. Zero for meters that do not move with usage.</summary>
        public decimal CostPerTurn { get; set; }
        public decimal QuantityPerDay { get; set; }
        public decimal QuantityPerTurn { get; set; }
    }

    /// <summary>
    /// Generates the synthetic agent-cost data behind the portal's "Agent costs" page: billed Copilot Studio
    /// credits per agent and per user, the tenant's Copilot Credits entitlement, and daily Azure spend.
    ///
    /// <para><b>The Copilot Studio figures are derived from the demo's own agent traffic</b> rather than
    /// invented independently, so the agent that the Copilot Adoption report shows as busiest is also the one
    /// the cost report shows as most expensive. A demo whose two pages disagreed about which agent matters
    /// would teach the reader the wrong thing about the product.</para>
    ///
    /// <para><b>Nothing is generated for a tenant that never used an agent.</b> Credits, per-user credits and
    /// the capacity snapshot are all written only where there is real synthetic usage behind them. Azure
    /// spend is the deliberate exception: a deployed AI Search index or App Service plan costs money whether
    /// or not anyone talks to the agent, so its fixed meters are billed every day and only the token meters
    /// follow usage.</para>
    ///
    /// <para><b>The per-agent and per-user totals are close but do not reconcile exactly</b>, which is the
    /// honest shape: Microsoft reports them through different endpoints, neither is a breakdown of the other,
    /// and the page says so. Here the difference comes from rounding each view at its own grain.</para>
    ///
    /// <para>Import-log rows are always written, including when there was nothing to import. That is what
    /// lets the report distinguish "the import is off", "the import failed" and "the import ran cleanly and
    /// this tenant has no Copilot Studio agents" - three states that otherwise all render as zero.</para>
    /// </summary>
    internal sealed class DemoAgentCosts
    {
        /// <summary>Every synthetic agent bills into the same synthetic tenant subscription.</summary>
        internal const string SubscriptionId = "00000000-0000-0000-0000-000000000000";
        internal const string Scope = "/subscriptions/" + SubscriptionId;
        internal const string Currency = "USD";

        /// <summary>
        /// The per-user unit the licensing API reports for the MCSMessages entitlement.
        /// </summary>
        internal const string UserCreditUnit = "Messages";

        /// <summary>
        /// A person who spent credits but is no longer in <c>dbo.users</c>. A real state the report is built
        /// to survive - the row keeps its spend and shows as unresolved - so the demo contains one.
        /// </summary>
        internal const string DepartedEntraObjectId = "00000000-0000-0000-0000-0000000000ff";

        /// <summary>Days at the end of the window whose Azure figures Microsoft has not finalised yet.</summary>
        internal const int EstimatedTrailingDays = 5;

        /// <summary>Days of import history written to the import log.</summary>
        internal const int ImportLogDays = 7;

        /// <summary>
        /// The four custom Copilot Studio agents the demo's audit data already contains, in the same order
        /// as the <c>copilot_agents</c> rows so agent 1 here is agent 1 there.
        /// </summary>
        /// <remarks>
        /// The fifth demo agent, "Microsoft 365 Copilot Cowork", is deliberately absent. It is not a Copilot
        /// Studio agent, so it does not appear in the Power Platform licensing API's per-agent consumption at
        /// all; listing it would invent a billing relationship this product cannot observe.
        /// <para>The identifiers are GUIDs rather than the <c>copilot_agents.agent_id</c> strings on purpose.
        /// The licensing API returns its own resource id, and nothing verifies that it matches the id the
        /// audit feed carries - which is exactly why <c>copilot_studio_credit_daily.agent_id</c> has no
        /// foreign key. Reusing the audit identifier here would quietly assert an equality the product
        /// refuses to assume.</para>
        /// </remarks>
        internal static readonly DemoBilledAgent[] Agents =
        {
            new DemoBilledAgent
            {
                AgentId = "00000000-0000-0000-0000-0000000000a1",
                AgentName = "Contoso Knowledge Assistant",
                EnvironmentId = "00000000-0000-0000-0000-0000000000e1",
                EnvironmentName = "Contoso (default)",
                Slices = new[]
                {
                    new DemoBilledSlice
                    {
                        FeatureName = "Generative answer", ChannelId = "msteams", LlmModel = "gpt-4o",
                        // Greek on a genuinely Unicode column: a knowledge source is customer-named text, and
                        // a demo that only ever wrote ASCII here would never prove the column round-trips.
                        KnowledgeSources = "Contoso intranet – Καλημέρα κόσμε",
                        TurnShare = 0.60m, CreditsPerTurn = 2m, UserShare = 0.90m, NonBilledShare = 0.10m,
                    },
                    new DemoBilledSlice
                    {
                        FeatureName = "Tenant graph grounding", ChannelId = "msteams", LlmModel = "gpt-4o",
                        KnowledgeSources = "Microsoft Graph (tenant)",
                        TurnShare = 0.30m, CreditsPerTurn = 10m, UserShare = 0.50m,
                    },
                    new DemoBilledSlice
                    {
                        FeatureName = "Classic answer", ChannelId = "webchat",
                        TurnShare = 0.10m, CreditsPerTurn = 1m, UserShare = 0.30m,
                    },
                },
            },
            new DemoBilledAgent
            {
                AgentId = "00000000-0000-0000-0000-0000000000a2",
                AgentName = "Contoso Sales Coach",
                EnvironmentId = "00000000-0000-0000-0000-0000000000e2",
                EnvironmentName = "Contoso Sales",
                Slices = new[]
                {
                    new DemoBilledSlice
                    {
                        FeatureName = "Generative answer", ChannelId = "msteams", LlmModel = "gpt-4o-mini",
                        KnowledgeSources = "Contoso product catalogue",
                        TurnShare = 0.50m, CreditsPerTurn = 2m, UserShare = 0.85m, NonBilledShare = 0.05m,
                    },
                    new DemoBilledSlice
                    {
                        FeatureName = "Agent action", ChannelId = "msteams", LlmModel = "gpt-4o",
                        ToolInvoked = "Contoso CRM: find account", KnowledgeSources = "Contoso product catalogue",
                        TurnShare = 0.30m, CreditsPerTurn = 5m, UserShare = 0.60m,
                    },
                    new DemoBilledSlice
                    {
                        FeatureName = "Agent flow actions", ChannelId = "directline",
                        ToolInvoked = "Create follow-up task",
                        TurnShare = 0.20m, CreditsPerTurn = 1.3m, UserShare = 0.40m,
                    },
                },
            },
            new DemoBilledAgent
            {
                AgentId = "00000000-0000-0000-0000-0000000000a3",
                AgentName = "Contoso Expenses Bot",
                EnvironmentId = "00000000-0000-0000-0000-0000000000e3",
                EnvironmentName = "Contoso Finance",
                Slices = new[]
                {
                    new DemoBilledSlice
                    {
                        FeatureName = "Classic answer", ChannelId = "webchat",
                        KnowledgeSources = "Contoso expenses policy",
                        TurnShare = 0.55m, CreditsPerTurn = 1m, UserShare = 0.95m,
                    },
                    new DemoBilledSlice
                    {
                        FeatureName = "Agent flow actions", ChannelId = "webchat",
                        ToolInvoked = "Submit expense claim",
                        TurnShare = 0.45m, CreditsPerTurn = 1.3m, UserShare = 0.50m, NonBilledShare = 0.20m,
                    },
                },
            },
            new DemoBilledAgent
            {
                AgentId = "00000000-0000-0000-0000-0000000000a4",
                AgentName = "Contoso Onboarding Guide",
                EnvironmentId = "00000000-0000-0000-0000-0000000000e4",
                EnvironmentName = "Contoso People",
                Slices = new[]
                {
                    new DemoBilledSlice
                    {
                        FeatureName = "Generative answer", ChannelId = "msteams", LlmModel = "gpt-4.1",
                        KnowledgeSources = "Contoso onboarding handbook",
                        TurnShare = 0.50m, CreditsPerTurn = 2m, UserShare = 0.90m,
                    },
                    new DemoBilledSlice
                    {
                        // Classifies as the GitHub Copilot harness, so the harness pivot has more than one value.
                        FeatureName = CopilotStudioHarnessClassifier.GitHubCopilotHarnessFeatureName,
                        ChannelId = "msteams", LlmModel = "gpt-4.1", ToolInvoked = "Provision starter kit",
                        TurnShare = 0.35m, CreditsPerTurn = 15m, UserShare = 0.40m,
                    },
                    new DemoBilledSlice
                    {
                        // Deliberately a feature name the classifier does not know, so the report's
                        // "unclassified harness" figure is exercised instead of being permanently zero.
                        FeatureName = "Autonomous task (preview)", ChannelId = "msteams", LlmModel = "gpt-4.1",
                        TurnShare = 0.15m, CreditsPerTurn = 3m, UserShare = 0.30m,
                    },
                },
            },
        };

        /// <summary>
        /// The Azure resources a tenant running these agents would be billed for. Two resource groups and
        /// six services so every Azure breakdown dimension the report offers has more than one value.
        /// </summary>
        internal static readonly DemoAzureMeter[] AzureMeters =
        {
            new DemoAzureMeter
            {
                ResourceGroup = "rg-contoso-demo-agents",
                ResourcePath = "Microsoft.CognitiveServices/accounts/contoso-demo-openai",
                ServiceName = "Azure OpenAI", MeterCategory = "Cognitive Services",
                MeterSubCategory = "Azure OpenAI", MeterName = "gpt-4o Input Tokens",
                CostPerTurn = 0.0021m, QuantityPerTurn = 1.4m,
            },
            new DemoAzureMeter
            {
                ResourceGroup = "rg-contoso-demo-agents",
                ResourcePath = "Microsoft.CognitiveServices/accounts/contoso-demo-openai",
                ServiceName = "Azure OpenAI", MeterCategory = "Cognitive Services",
                MeterSubCategory = "Azure OpenAI", MeterName = "gpt-4o Output Tokens",
                CostPerTurn = 0.0084m, QuantityPerTurn = 0.6m,
            },
            new DemoAzureMeter
            {
                ResourceGroup = "rg-contoso-demo-agents",
                ResourcePath = "Microsoft.Search/searchServices/contoso-demo-search",
                ServiceName = "Azure AI Search", MeterCategory = "Azure Cognitive Search",
                MeterSubCategory = "Standard", MeterName = "S1 Search Unit",
                CostPerDay = 8.21m, QuantityPerDay = 24m,
            },
            new DemoAzureMeter
            {
                ResourceGroup = "rg-contoso-demo-agents",
                ResourcePath = "Microsoft.Web/serverfarms/contoso-demo-plan",
                ServiceName = "Azure App Service", MeterCategory = "Azure App Service",
                MeterSubCategory = "Premium v3 Plan", MeterName = "P1 v3 App Hours",
                CostPerDay = 4.09m, QuantityPerDay = 24m,
            },
            new DemoAzureMeter
            {
                ResourceGroup = "rg-contoso-demo-platform",
                ResourcePath = "Microsoft.Sql/servers/contoso-demo-sql/databases/contoso-demo-analytics",
                ServiceName = "SQL Database", MeterCategory = "SQL Database",
                MeterSubCategory = "General Purpose - Serverless", MeterName = "vCore",
                CostPerDay = 3.12m, QuantityPerDay = 12m,
            },
            new DemoAzureMeter
            {
                ResourceGroup = "rg-contoso-demo-platform",
                ResourcePath = "Microsoft.Storage/storageAccounts/contosodemostorage",
                ServiceName = "Storage", MeterCategory = "Storage",
                MeterSubCategory = "General Block Blob v2", MeterName = "Hot LRS Data Stored",
                CostPerDay = 0.42m, QuantityPerDay = 18m,
            },
            new DemoAzureMeter
            {
                ResourceGroup = "rg-contoso-demo-platform",
                ResourcePath = "Microsoft.OperationalInsights/workspaces/contoso-demo-logs",
                ServiceName = "Azure Monitor", MeterCategory = "Azure Monitor",
                MeterSubCategory = "Log Analytics", MeterName = "Data Ingestion",
                CostPerDay = 0.65m, CostPerTurn = 0.0002m, QuantityPerDay = 0.3m, QuantityPerTurn = 0.0001m,
            },
        };

        private readonly DemoOptions _options;
        private readonly IDemoSink _sink;
        private readonly int[,] _agentTurns;
        private readonly int[,] _agentUsers;
        private readonly int[] _userRowsByDay;
        private readonly decimal[] _creditsByDay;
        private readonly int[] _creditRowsByDay;
        private readonly DateTime _importedUtc;

        public DemoAgentCosts(DemoOptions options, IDemoSink sink)
        {
            // Agent ids 1..Agents.Length are the Copilot Studio agents and the id above them is the Cowork
            // surface, which is not billed through the Power Platform licensing API. If a fifth Copilot
            // Studio agent is ever added to the demo, DemoTimeline must make room for it first - otherwise
            // Cowork's turns would silently be billed as Copilot Studio credits.
            if (Agents.Length >= DemoTimeline.MaxAgentId)
                throw new InvalidOperationException("The billed agents would overlap the Cowork agent id.");

            _options = options;
            _sink = sink;
            _agentTurns = new int[Agents.Length, options.Days];
            _agentUsers = new int[Agents.Length, options.Days];
            _userRowsByDay = new int[options.Days];
            _creditsByDay = new decimal[options.Days];
            _creditRowsByDay = new int[options.Days];
            // Fixed, not the wall clock: every generated row must be reproducible from the seed and --as-of.
            _importedUtc = options.AsOf.AddHours(2);
        }

        /// <summary>
        /// Records one user's agent turns for one reported day, and writes that user's own billed row.
        /// </summary>
        /// <param name="turnsByAgent">
        /// Turns per agent for the day, indexed by the demo agent id (1-based); index 0 and any agent beyond
        /// <see cref="Agents"/> - Microsoft 365 Copilot Cowork - are ignored.
        /// </param>
        public void AddUserAgentDay(DemoUser user, int dayIndex, int[] turnsByAgent)
        {
            int busiest = 0, total = 0;
            for (int agent = 1; agent <= Agents.Length; agent++)
            {
                if (turnsByAgent[agent] <= 0) continue;
                total += turnsByAgent[agent];
                _agentTurns[agent - 1, dayIndex] += turnsByAgent[agent];
                _agentUsers[agent - 1, dayIndex]++;
                if (busiest == 0 || turnsByAgent[agent] > turnsByAgent[busiest]) busiest = agent;
            }
            if (busiest == 0) return;

            // The per-user endpoint reports a person's consumption per environment and carries no agent, so
            // the day lands on the environment of the agent they used most rather than being split.
            var environment = Agents[busiest - 1];
            var credits = decimal.Round(total * environment.CreditsPerTurn, 2);
            if (credits <= 0m) return;

            WriteUserRow(dayIndex, DemoPopulation.AzureAdObjectId(_options.Seed, user.Id), user.Id,
                environment.EnvironmentId, environment.EnvironmentName, credits);
        }

        /// <summary>
        /// Writes everything that can only be known once every user's timeline has been seen: the per-agent
        /// billed rows, the tenant capacity snapshots, Azure spend and the import log.
        /// </summary>
        public void Write()
        {
            WriteCredits();
            WriteDepartedUserCredits();
            WriteCapacity();
            WriteAzureCosts();
            WriteImportLog();
        }

        private void WriteCredits()
        {
            for (int agent = 0; agent < Agents.Length; agent++)
            {
                var billed = Agents[agent];
                for (int d = 0; d < _options.Days; d++)
                {
                    int turns = _agentTurns[agent, d], users = _agentUsers[agent, d];
                    if (turns <= 0) continue;
                    var date = _options.Start.AddDays(d);

                    foreach (var slice in billed.Slices)
                    {
                        var credits = decimal.Round(turns * slice.TurnShare * slice.CreditsPerTurn, 2);
                        if (credits <= 0m) continue;
                        decimal? nonBilled = slice.NonBilledShare > 0m
                            ? decimal.Round(credits * slice.NonBilledShare, 2)
                            : (decimal?)null;

                        _sink.Write(DemoTables.StudioCredits, date, billed.EnvironmentId, billed.EnvironmentName,
                            billed.AgentId, billed.AgentName, CopilotStudioHarnessClassifier.Classify(slice.FeatureName),
                            slice.FeatureName, slice.ChannelId, slice.LlmModel, slice.ToolInvoked, slice.KnowledgeSources,
                            credits, nonBilled, SliceUsers(users, slice.UserShare), LastRefreshed(date),
                            AgentCostRowHasher.Hash(HashDate(date), billed.EnvironmentId, billed.AgentId,
                                slice.FeatureName, slice.ChannelId, slice.LlmModel, slice.ToolInvoked, slice.KnowledgeSources),
                            _importedUtc);

                        _creditsByDay[d] += credits;
                        _creditRowsByDay[d]++;
                    }
                }
            }
        }

        /// <summary>
        /// Spend that belongs to somebody who has since left the directory, so the report's unresolved-user
        /// path is exercised rather than only ever seeing rows that resolve.
        /// </summary>
        private void WriteDepartedUserCredits()
        {
            var environment = Agents[0];
            for (int d = 0; d < _options.Days; d += 7)
            {
                if (_agentTurns[0, d] <= 0) continue;
                WriteUserRow(d, DepartedEntraObjectId, null, environment.EnvironmentId,
                    environment.EnvironmentName, decimal.Round(4m + d % 5, 2));
            }
        }

        private void WriteUserRow(int dayIndex, string entraObjectId, int? userId,
            string environmentId, string environmentName, decimal credits)
        {
            var date = _options.Start.AddDays(dayIndex);
            _sink.Write(DemoTables.StudioUserCredits, date, entraObjectId, userId, environmentId, environmentName,
                null, credits, UserCreditUnit,
                AgentCostRowHasher.Hash(HashDate(date), entraObjectId, environmentId), _importedUtc);
            _userRowsByDay[dayIndex]++;
        }

        /// <summary>
        /// A daily snapshot of the tenant's Copilot Credits entitlement. Written only when there is billed
        /// consumption to snapshot: an entitlement figure on a tenant with no Copilot Studio spend would be
        /// a number the product could not have observed.
        /// </summary>
        private void WriteCapacity()
        {
            decimal entitled = Entitlement();
            if (entitled <= 0m) return;

            decimal monthToDate = 0m;
            int month = -1;
            for (int d = 0; d < _options.Days; d++)
            {
                var date = _options.Start.AddDays(d);
                if (date.Month != month) { month = date.Month; monthToDate = 0m; }
                monthToDate += _creditsByDay[d];

                decimal consumed = decimal.Round(monthToDate, 2);
                decimal available = Math.Max(0m, entitled - consumed);
                decimal payAsYouGo = Math.Max(0m, consumed - entitled);
                _sink.Write(DemoTables.StudioCapacity, date.AddHours(3), date, entitled, consumed, "MonthToDate",
                    entitled, available, payAsYouGo, payAsYouGo > 0m ? "Overage" : "WithinCapacity");
            }
        }

        /// <summary>
        /// Purchased capacity, sized from the busiest calendar month in the window so the headroom the report
        /// shows stays sensible whether the demo has forty users or four hundred thousand.
        /// </summary>
        private decimal Entitlement()
        {
            decimal busiestMonth = 0m, monthToDate = 0m;
            int month = -1;
            for (int d = 0; d < _options.Days; d++)
            {
                var date = _options.Start.AddDays(d);
                if (date.Month != month) { month = date.Month; monthToDate = 0m; }
                monthToDate += _creditsByDay[d];
                if (monthToDate > busiestMonth) busiestMonth = monthToDate;
            }
            if (busiestMonth <= 0m) return 0m;

            // Capacity is bought in packs, so round up rather than reporting an oddly exact entitlement.
            decimal wanted = busiestMonth * 1.2m;
            decimal pack = wanted >= 10000m ? 1000m : 100m;
            return Math.Ceiling(wanted / pack) * pack;
        }

        private void WriteAzureCosts()
        {
            for (int d = 0; d < _options.Days; d++)
            {
                var date = _options.Start.AddDays(d);
                int turns = 0;
                for (int agent = 0; agent < Agents.Length; agent++) turns += _agentTurns[agent, d];
                bool estimated = d >= _options.Days - EstimatedTrailingDays;

                for (int meter = 0; meter < AzureMeters.Length; meter++)
                {
                    var billed = AzureMeters[meter];
                    // Small deterministic drift so the daily figures are not a flat line; real Azure meters
                    // move a little day to day even when nothing changes.
                    decimal jitter = 0.92m + DemoRandom.Value(_options.Seed, meter, d, 60) % 17 * 0.01m;
                    decimal cost = decimal.Round(billed.CostPerDay * jitter + billed.CostPerTurn * turns, 4);
                    decimal quantity = decimal.Round(billed.QuantityPerDay * jitter + billed.QuantityPerTurn * turns, 4);
                    if (cost <= 0m) continue;

                    var resourceId = Scope + "/resourceGroups/" + billed.ResourceGroup + "/providers/" + billed.ResourcePath;
                    _sink.Write(DemoTables.AzureCosts, date, Scope, SubscriptionId, resourceId, billed.ResourceGroup,
                        billed.ServiceName, billed.MeterCategory, billed.MeterSubCategory, billed.MeterName,
                        cost, Currency, quantity, estimated,
                        AgentCostRowHasher.Hash(HashDate(date), Scope, SubscriptionId, resourceId, billed.ServiceName,
                            billed.MeterCategory, billed.MeterSubCategory, billed.MeterName, Currency),
                        _importedUtc);
                }
            }
        }

        /// <summary>
        /// One clean run per import per day for the last week of the window.
        /// </summary>
        /// <remarks>
        /// Always written, even when the tenant produced no billed rows at all. The report reads the latest
        /// log entry to tell "the import has never run" apart from "the import ran, succeeded, and this
        /// tenant simply has no Copilot Studio agents" - and without a log row the second case is invisible.
        /// </remarks>
        private void WriteImportLog()
        {
            int first = Math.Max(0, _options.Days - ImportLogDays);
            decimal entitled = Entitlement();
            for (int d = first; d < _options.Days; d++)
            {
                var date = _options.Start.AddDays(d);
                // Each importer re-reads a trailing window because both sources restate history, so the
                // logged row counts cover that window rather than the single day it ended on.
                int windowStart = Math.Max(0, d - ImportLogDays + 1);
                var from = _options.Start.AddDays(windowStart);
                var stamp = date.AddHours(4);

                int credits = 0, users = 0, azure = 0;
                for (int day = windowStart; day <= d; day++)
                {
                    credits += _creditRowsByDay[day];
                    users += _userRowsByDay[day];
                    azure += AzureRowsOn(day);
                }

                Log(AgentCostImportNames.CopilotStudioCredits, stamp, from, date, credits);
                Log(AgentCostImportNames.CopilotStudioUserCredits, stamp, from, date, users);
                // A capacity read is a point-in-time snapshot of the whole entitlement, not a date window.
                Log(AgentCostImportNames.CopilotStudioCapacity, stamp, null, null, entitled > 0m ? 1 : 0);
                Log(AgentCostImportNames.AzureCostManagement, stamp, from, date, azure);
            }
        }

        private void Log(string importName, DateTime stamp, DateTime? from, DateTime? to, int rows)
        {
            _sink.Write(DemoTables.AgentCostImports, importName, stamp, from, to, rows, rows, null);
        }

        private int AzureRowsOn(int dayIndex)
        {
            int turns = 0, rows = 0;
            for (int agent = 0; agent < Agents.Length; agent++) turns += _agentTurns[agent, dayIndex];
            foreach (var meter in AzureMeters)
                if (meter.CostPerDay > 0m || turns > 0) rows++;
            return rows;
        }

        /// <summary>
        /// How many of the day's users reached this slice. Never summed by the report - the same person
        /// appears under every slice they touched - so it is capped at the agent's own daily user count.
        /// </summary>
        private static int SliceUsers(int users, decimal share) =>
            Math.Max(1, Math.Min(users, (int)Math.Round(users * share, MidpointRounding.AwayFromZero)));

        /// <summary>
        /// When Microsoft last recalculated the day, which settles the day after use and never postdates the
        /// import that read it.
        /// </summary>
        private DateTime LastRefreshed(DateTime usageDate)
        {
            var refreshed = usageDate.AddDays(1).AddHours(6);
            return refreshed > _importedUtc ? _importedUtc : refreshed;
        }

        private static string HashDate(DateTime date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
}
