using Common.Entities.Copilot;
using Common.Entities.PromptCategories;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using Tests.FakeDataGen.Seeding;
using CreditEstimation = WebJob.Office365ActivityImporter.Engine.ActivityAPI.Copilot.CopilotCreditEstimation;
using EstimateEvent = WebJob.Office365ActivityImporter.Engine.ActivityAPI.Copilot.CostEstimate.CopilotAuditEvent;
using EstimateMessage = WebJob.Office365ActivityImporter.Engine.ActivityAPI.Copilot.CostEstimate.Message;
using EstimateModel = WebJob.Office365ActivityImporter.Engine.ActivityAPI.Copilot.CostEstimate.ModelTransparencyDetail;
using EstimateResource = WebJob.Office365ActivityImporter.Engine.Entities.Serialisation.AccessedResource;

namespace Tests.FakeDataGen.Demo
{
    /// <summary>The size and shape of one prompt or response, as Graph's interaction history reports it.</summary>
    internal struct DemoMessageShape
    {
        public int Words, Chars, Attachments, Links, Mentions, Contexts;
    }

    /// <summary>What the audit record of one Copilot turn read, and what the importer would estimate it cost.</summary>
    internal struct DemoChatFacts
    {
        /// <summary>Accessed-resource name ids; 0 means none. Two set ids are never equal.</summary>
        public int FirstResource, SecondResource;

        /// <summary><c>copilot_chats.copilot_credit_estimate_total</c>.</summary>
        public int Estimate;

        /// <summary><c>copilot_chats.copilot_credit_estimate_json</c>; null on a turn without an agent.</summary>
        public string EstimateJson;
    }

    /// <summary>
    /// What each synthetic person uses Copilot for: the prompt categories their department and the app they are
    /// in make likely, the key phrases Azure AI Language would pull out of those prompts, the files and sites
    /// Copilot reads to answer them, and the credit estimate the importer works out for an agent's turn.
    /// </summary>
    /// <remarks>
    /// <para>Every value is a pure function of (seed, user, day, slot) through <see cref="DemoRandom"/>, using
    /// salts of its own (3000-3799). The timeline's draws - turns, hosts and agents - are untouched, so the adoption,
    /// investment and spend figures that derive from them do not move, a person's detail is reproducible from the
    /// seed, and nothing is held per user: memory stays O(1) per person at a million users.</para>
    /// <para>The credit estimate is produced by the importer's own <see cref="CreditEstimation"/>, on a synthetic
    /// audit record that mirrors the resources this class writes, so the stored JSON is exactly what an import
    /// would store and the +10 tenant-grounding charge applies precisely when the turn read a tenant resource.
    /// Each agent's mix of answers, grounding and deep reasoning is tuned so its mean estimate per turn lands near
    /// the blended <see cref="DemoBilledAgent.CreditsPerTurn"/> it is billed at (see <see cref="DemoAgentCosts"/>):
    /// the estimate and the billed ledger tell the same story without matching exactly.</para>
    /// <para>Prompt text is never stored by the product, so nothing here invents any.</para>
    /// </remarks>
    internal sealed class DemoCopilotPersonas
    {
        public const int CoworkAgent = 5;

        /// <summary>
        /// The first keyword id used for prompt key phrases. The Teams channel keywords keep ids 1-5, so adding
        /// prompt phrases leaves every Teams row unchanged.
        /// </summary>
        public const int FirstKeywordId = 6;

        public const int MaxKeywordsPerPrompt = 3;

        /// <summary>
        /// Audit agent ids, indexed by the demo agent id (1-based). Agents 1-4 use the documented Copilot Studio
        /// custom-engine shape, which is the only kind the importer prices; agent 5 is Microsoft's Cowork, which it
        /// never prices. The GUIDs deliberately differ from the billing ledger's agent ids: the product does not
        /// assume the audit and billing feeds share one.
        /// </summary>
        public static readonly string[] AgentIds =
        {
            null,
            "CopilotStudio.CustomEngine.00000000-0000-0000-0000-0000000000c1",
            "CopilotStudio.CustomEngine.00000000-0000-0000-0000-0000000000c2",
            "CopilotStudio.CustomEngine.00000000-0000-0000-0000-0000000000c3",
            "CopilotStudio.CustomEngine.00000000-0000-0000-0000-0000000000c4",
            "Copilot.M365Copilot.CoworkAgent"
        };

        // Prompt-category indices in PromptCategoryConfiguration.Defaults() order. Categories are resolved by id,
        // never by position, so a reordered taxonomy cannot silently relabel the demo.
        private const int MS = 0, DE = 1, IL = 2, CD = 3, AN = 4, DR = 5, DPS = 6, OT = 7;
        internal static readonly string[] CategoryIds =
        {
            "meeting-summary", "document-editing", "information-lookup", "content-drafting",
            "analysis", "delegated-research", "delegated-problem-solving", "other"
        };
        private const int OtherPerMille = 40;
        private const int FavouriteWeight = 12;

        // Weights per department for MS, DE, IL, CD, AN, DR, DPS. "other" is a flat 4% on top.
        private static readonly Dictionary<string, int[]> DepartmentWeights = new Dictionary<string, int[]>(StringComparer.Ordinal)
        {
            ["Engineering"] = new[] { 8, 8, 16, 6, 10, 8, 42 },
            ["Product"] = new[] { 18, 14, 14, 16, 14, 14, 10 },
            ["Design"] = new[] { 12, 16, 12, 26, 8, 16, 10 },
            ["Sales"] = new[] { 20, 8, 18, 34, 8, 7, 5 },
            ["Marketing"] = new[] { 10, 14, 10, 44, 8, 10, 4 },
            ["Finance"] = new[] { 8, 20, 12, 8, 38, 8, 6 },
            ["Human Resources"] = new[] { 12, 14, 24, 30, 6, 8, 6 },
            ["Legal"] = new[] { 6, 40, 14, 10, 6, 20, 4 },
            ["Operations"] = new[] { 22, 8, 14, 8, 30, 6, 12 },
            ["Customer Support"] = new[] { 8, 6, 34, 26, 6, 6, 14 },
            ["IT"] = new[] { 8, 6, 26, 6, 10, 10, 34 },
            ["Research & Development"] = new[] { 8, 10, 12, 8, 24, 30, 8 },
            ["Executive"] = new[] { 36, 8, 10, 8, 10, 22, 6 }
        };

        // Added to the department weights by the app the prompt is typed in, indexed like DemoTimeline.Hosts.
        private static readonly int[][] HostBoosts =
        {
            new[] { 0, 0, 15, 0, 0, 12, 0 },  // bizchat: look-ups and research
            new[] { 25, 0, 5, 0, 0, 0, 0 },   // Teams: meeting recaps
            new[] { 0, 18, 0, 12, 0, 0, 0 },  // Word
            new[] { 6, 0, 0, 20, 0, 0, 0 },   // Outlook
            new[] { 0, 0, 0, 0, 24, 0, 0 },   // Excel
            new[] { 0, 6, 0, 18, 0, 0, 0 },   // PowerPoint
            new[] { 12, 0, 0, 6, 0, 0, 0 },   // OneNote
            new[] { 0, 0, 0, 0, 0, 18, 18 }   // Cowork: delegated work
        };

        // Added by the agent that answers, indexed by the demo agent id.
        private static readonly int[][] AgentBoosts =
        {
            new int[7],
            new[] { 0, 0, 10, 0, 0, 6, 0 },   // Knowledge Assistant
            new[] { 0, 0, 0, 12, 6, 0, 0 },   // Sales Coach
            new[] { 0, 0, 10, 0, 0, 0, 8 },   // Expenses Bot
            new[] { 0, 0, 12, 0, 0, 0, 0 },   // Onboarding Guide
            new int[7]                        // Cowork: the host already says "delegated"
        };

        // Word ranges per category, MS..OT.
        private static readonly int[,] PromptWords = { { 8, 20 }, { 15, 40 }, { 6, 15 }, { 25, 60 }, { 12, 35 }, { 25, 60 }, { 20, 50 }, { 4, 20 } };
        private static readonly int[,] ResponseWords = { { 80, 250 }, { 120, 350 }, { 40, 120 }, { 120, 350 }, { 100, 300 }, { 200, 500 }, { 150, 400 }, { 30, 100 } };

        private static readonly Phrase[] CommonPhrases =
        {
            P("action items", MS), P("meeting notes", MS), P("key takeaways", MS), P("next steps", MS),
            P("status update", CD), P("executive summary", CD), P("follow-up email", CD),
            P("project timeline", IL),
            P("team offsite", OT), P("travel booking", OT), P("lunch and learn", OT)
        };

        private static readonly Dictionary<string, Phrase[]> DepartmentPhrases = new Dictionary<string, Phrase[]>(StringComparer.Ordinal)
        {
            ["Engineering"] = new[]
            {
                P("incident postmortem", DPS), P("deployment pipeline", DPS), P("build failure", DPS),
                P("API design review", DE), P("architecture decision record", DE),
                P("unit test coverage", AN), P("performance regression", AN), P("release notes", CD),
                P("code review checklist", IL), P("on-call handover", IL), P("sprint retrospective", MS)
            },
            ["Product"] = new[]
            {
                P("product roadmap", DE), P("product requirements document", DE),
                P("feature prioritisation", AN), P("customer feedback themes", AN), P("user story", CD),
                P("launch checklist", IL), P("beta programme", IL),
                P("competitive landscape", DR), P("market sizing", DR), P("stakeholder review", MS)
            },
            ["Design"] = new[]
            {
                P("design critique", MS), P("prototype walkthrough", MS),
                P("accessibility audit", DR), P("user research synthesis", DR),
                P("style guide", DE), P("wireframe feedback", DE),
                P("persona refresh", CD), P("usability test plan", CD),
                P("brand guidelines", IL), P("icon set", IL)
            },
            ["Sales"] = new[]
            {
                P("pipeline review", MS), P("deal summary", MS),
                P("renewal proposal", CD), P("customer follow-up email", CD), P("Προσφορά πελάτη", CD),
                P("Fabrikam account plan", DE), P("quarterly forecast", AN),
                P("discount approval", IL), P("objection handling", IL), P("Northwind opportunity", IL),
                P("competitor pricing", DR)
            },
            ["Marketing"] = new[]
            {
                P("campaign brief", CD), P("social media calendar", CD), P("product launch blog post", CD),
                P("webinar invitation", CD), P("newsletter copy", CD),
                P("brand messaging", DE), P("press release", DE),
                P("campaign performance", AN), P("audience segmentation", AN), P("event recap", MS)
            },
            ["Finance"] = new[]
            {
                P("budget variance", AN), P("invoice reconciliation", AN), P("cash flow forecast", AN),
                P("cost centre report", AN), P("Προϋπολογισμός 2027", AN),
                P("month-end close", IL), P("expense policy", IL),
                P("audit findings", DE), P("vendor payment terms", DE), P("board financial pack", DE),
                P("quarterly earnings summary", CD)
            },
            ["Human Resources"] = new[]
            {
                P("job description", CD), P("interview questions", CD), P("offer letter", CD),
                P("onboarding checklist", IL), P("benefits enrolment", IL), P("leave policy", IL),
                P("performance review", DE), P("training plan", DE), P("Αξιολόγηση απόδοσης", DE),
                P("engagement survey results", AN), P("wellbeing programme", DR)
            },
            ["Legal"] = new[]
            {
                P("contract redline", DE), P("NDA review", DE), P("data processing agreement", DE),
                P("trademark filing", DE), P("Σύμβαση πλαισίου", DE),
                P("compliance obligations", DR), P("case law research", DR),
                P("clause comparison", AN), P("regulatory update", IL), P("litigation hold", IL)
            },
            ["Operations"] = new[]
            {
                P("supply chain delays", AN), P("inventory levels", AN), P("warehouse capacity", AN),
                P("vendor scorecard", AN), P("shift schedule", DE), P("standard operating procedure", DE),
                P("operations review", MS), P("process improvement", DPS),
                P("logistics update", CD), P("safety incident report", CD)
            },
            ["Customer Support"] = new[]
            {
                P("ticket backlog", AN), P("customer satisfaction trends", AN), P("escalation summary", MS),
                P("refund request", IL), P("known issue workaround", IL), P("password reset steps", IL),
                P("customer reply template", CD), P("knowledge base article", CD),
                P("SLA breach", DPS), P("troubleshooting guide", DPS)
            },
            ["IT"] = new[]
            {
                P("device enrolment", IL), P("licence assignment", IL),
                P("conditional access policy", DPS), P("PowerShell script", DPS), P("VPN connectivity", DPS),
                P("mailbox migration", DPS), P("security alert triage", DPS),
                P("patch schedule", DE), P("service desk runbook", DE), P("change advisory board", MS)
            },
            ["Research & Development"] = new[]
            {
                P("literature review", DR), P("patent landscape", DR), P("technology scouting", DR),
                P("materials comparison", DR), P("experiment results", AN), P("prototype test data", AN),
                P("statistical significance", AN), P("grant proposal", CD), P("research summary", CD),
                P("lab protocol", DE)
            },
            ["Executive"] = new[]
            {
                P("board briefing", MS), P("strategy offsite", MS), P("quarterly business review", MS),
                P("leadership team update", CD), P("all-hands talking points", CD),
                P("market outlook", DR), P("acquisition due diligence", DR), P("investor questions", DR),
                P("talent strategy", DR), P("operating plan", AN)
            }
        };

        // Accessed resources. Ids 1-5 keep the names, sites and types they always had: DLP events point at them.
        private static readonly string[] LegacyResourceNames =
        {
            "Contoso knowledge – Καλημέρα κόσμε", "Contoso planning workbook", "Contoso customer message",
            "Contoso project page", "Contoso delivery list"
        };
        private static readonly string[] LegacySites =
        {
            "https://contoso.sharepoint.com/sites/demo-01", "https://contoso.sharepoint.com/sites/demo-02",
            "https://outlook.office365.com/owa", "https://contoso.example/knowledge",
            "https://contoso.sharepoint.com/sites/demo-03"
        };
        private static readonly string[] TypeNames = { null, "File", "Email", "Message", "WebPage", "ListItem" };
        private const int FileType = 1, EmailType = 2, MessageType = 3, WebPageType = 4;
        private const int KnowledgeLibrary = 1, ExternalProjectPage = 4, DeliveryList = 5;
        private const int OutlookSite = 3, LegacyCount = 5;
        private const string TeamsSite = "https://teams.microsoft.com", InsightsSite = "https://www.contoso.example/insights";

        // A department's own resources, in id order: a Word document, a workbook, a deck, a mail thread and a
        // channel post.
        private const int Docx = 0, Xlsx = 1, Pptx = 2, Mail = 3, Post = 4, DepartmentKinds = 5;

        private static readonly Dictionary<string, string> SiteSlugs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Engineering"] = "engineering", ["Product"] = "product", ["Design"] = "design", ["Sales"] = "sales",
            ["Marketing"] = "marketing", ["Finance"] = "finance", ["Human Resources"] = "hr", ["Legal"] = "legal",
            ["Operations"] = "operations", ["Customer Support"] = "customer-support", ["IT"] = "it",
            ["Research & Development"] = "research", ["Executive"] = "executive"
        };

        private static readonly Dictionary<string, string[]> DepartmentFiles = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["Engineering"] = new[] { "Architecture decision record – payments service.docx", "Sprint capacity plan.xlsx",
                "Engineering all-hands Q3.pptx", "RE: Release 4.2 go/no-go", "Engineering channel – incident 4721 bridge" },
            ["Product"] = new[] { "Product requirements – mobile check-in.docx", "Feature prioritisation matrix.xlsx",
                "Product roadmap H1 FY27.pptx", "RE: Beta programme feedback", "Product channel – launch readiness" },
            ["Design"] = new[] { "Usability test plan – onboarding flow.docx", "Accessibility audit tracker.xlsx",
                "Design critique – checkout redesign.pptx", "RE: Brand refresh review", "Design channel – prototype feedback" },
            ["Sales"] = new[] { "Customer renewal proposal – Fabrikam.docx", "FY27 pipeline – Contoso.xlsx",
                "Sales kickoff deck.pptx", "RE: Northwind pricing question", "Sales channel – deal desk" },
            ["Marketing"] = new[] { "Campaign brief – spring launch.docx", "Campaign performance dashboard.xlsx",
                "Brand messaging framework.pptx", "RE: Webinar invitation draft", "Marketing channel – social calendar" },
            ["Finance"] = new[] { "Expense policy FY27.docx", "Budget variance FY27.xlsx",
                "Board update Q3.pptx", "RE: Month-end close checklist", "Finance channel – invoice approvals" },
            ["Human Resources"] = new[] { "Job description – senior analyst.docx", "Engagement survey results.xlsx",
                "New starter induction.pptx", "RE: Benefits enrolment window", "HR channel – onboarding questions" },
            ["Legal"] = new[] { "Master services agreement – Tailspin.docx", "Contract obligations register.xlsx",
                "Regulatory update briefing.pptx", "RE: NDA redline – Woodgrove", "Legal channel – contract requests" },
            ["Operations"] = new[] { "Standard operating procedure – returns.docx", "Inventory levels – week 42.xlsx",
                "Operations review Q3.pptx", "RE: Carrier delay update", "Operations channel – shift handover" },
            ["Customer Support"] = new[] { "Troubleshooting guide – sign-in issues.docx", "Ticket backlog by queue.xlsx",
                "Support quality review.pptx", "RE: Escalation – Adventure Works order", "Support channel – known issues" },
            ["IT"] = new[] { "Service desk runbook.docx", "Device enrolment status.xlsx",
                "Change advisory board – October.pptx", "RE: Planned maintenance window", "IT channel – security alerts" },
            ["Research & Development"] = new[] { "Literature review – battery materials.docx", "Experiment results – trial 7.xlsx",
                "Research portfolio review.pptx", "RE: Lab equipment booking", "Research channel – prototype tests" },
            ["Executive"] = new[] { "Strategy memo FY27.docx", "Operating plan FY27.xlsx",
                "Board briefing – October.pptx", "RE: Leadership offsite agenda", "Leadership channel – weekly priorities" }
        };

        // Greek file names on Unicode columns, used in place of a department's docx/xlsx a quarter of the time.
        private static readonly string[] GreekFiles =
            { "Προσφορά πελάτη – Contoso.docx", "Προϋπολογισμός 2027.xlsx", "Σύμβαση πλαισίου – Contoso.docx" };
        private static readonly string[] ExternalPages =
            { "Industry outlook 2027 – Contoso Insights", "Contoso public product documentation", "Market trends briefing – Contoso Insights" };

        /// <summary>How an agent's turns are composed. Percentages; see the class remarks for the calibration.</summary>
        private sealed class AgentMix
        {
            /// <summary>Turns logged as Copilot Studio's runtime record, which carries no messages: priced at 0 (NoMessages).</summary>
            public int RuntimePercent;
            public int OneAnswerPercent, UpToTwoAnswersPercent;
            /// <summary>Turns grounded on tenant content, each answer of which carries the +10 grounding charge.</summary>
            public int GroundedPercent;
            /// <summary>Turns that invoke deep reasoning, the estimator's +5 agent action.</summary>
            public int DeepPercent;
            /// <summary>Ungrounded turns that read a public web page, which is not tenant grounding.</summary>
            public int ExternalPercent;
        }

        // Expected mean estimate per turn vs the blended billed CreditsPerTurn:
        //   Knowledge  0.85 * 1.15 answers * (2 + 10 * 0.25)               = 4.40 vs 4.30
        //   Sales      0.75 * (1.06 * (2 + 10 * 0.03) + 5 * 0.25)          = 2.77 vs 2.76
        //   Expenses   0.35 * (2 + 5 * 0.25)                               = 1.14 vs 1.135
        //   Onboarding 0.95 * (1.26 * (2 + 10 * 0.31) + 5 * 0.15)          = 6.82 vs 6.70
        private static readonly AgentMix[] Mixes =
        {
            null,
            new AgentMix { RuntimePercent = 15, OneAnswerPercent = 88, UpToTwoAnswersPercent = 97, GroundedPercent = 25, DeepPercent = 0, ExternalPercent = 30 },
            new AgentMix { RuntimePercent = 25, OneAnswerPercent = 95, UpToTwoAnswersPercent = 99, GroundedPercent = 3, DeepPercent = 25, ExternalPercent = 30 },
            new AgentMix { RuntimePercent = 65, OneAnswerPercent = 100, UpToTwoAnswersPercent = 100, GroundedPercent = 0, DeepPercent = 25, ExternalPercent = 0 },
            new AgentMix { RuntimePercent = 5, OneAnswerPercent = 78, UpToTwoAnswersPercent = 96, GroundedPercent = 31, DeepPercent = 15, ExternalPercent = 0 }
        };
        private const string DeepReasoningModel = "DEEP_LEO";

        private static readonly CopilotAgentClassification[] AgentKinds;
        private static readonly int[][] Weights;
        private static readonly int[][] Favourites;
        private static readonly string[] KeywordTexts;
        private static readonly int[][][] Candidates;
        private static readonly string[] ResourceNameTexts;
        private static readonly string[] SiteUrls;
        private static readonly int[] ResourceSite, ResourceType;
        private static readonly int[] GreekDocx, GreekXlsx;
        private static readonly int FirstExternalPage, SalesDepartment, HrDepartment;

        private readonly int _seed;
        private readonly PromptCategoryDefinition[] _categories;
        private readonly Dictionary<int, KeyValuePair<int, string>> _estimates = new Dictionary<int, KeyValuePair<int, string>>();

        static DemoCopilotPersonas()
        {
            var departments = SeedDataCatalogue.Departments;
            int count = departments.Length;
            if (HostBoosts.Length != DemoTimeline.Hosts.Length)
                throw new InvalidOperationException("Every Copilot host needs a prompt-category affinity.");
            if (AgentBoosts.Length != AgentIds.Length || Mixes.Length != CoworkAgent)
                throw new InvalidOperationException("Every demo agent needs a prompt-category affinity and a turn mix.");
            AgentKinds = AgentIds.Select(id => id == null ? CopilotAgentClassification.Unclassified
                : CopilotAgentClassifier.ClassifyStoredAgentId(id)).ToArray();

            Weights = new int[count][];
            Favourites = new int[count][];
            for (int d = 0; d < count; d++)
            {
                Weights[d] = Require(DepartmentWeights, departments[d], "prompt-category weights");
                if (Weights[d].Length != OT) throw new InvalidOperationException("Category weights for " + departments[d] + " must cover every category but 'other'.");
                // A person's favourite is one of their department's three most common categories.
                var weights = Weights[d];
                Favourites[d] = Enumerable.Range(0, OT).OrderByDescending(c => weights[c]).ThenBy(c => c).Take(3).ToArray();
            }

            // Key phrases: the cross-department pool first, then each department in catalogue order.
            var texts = new List<string>(CommonPhrases.Select(p => p.Text));
            var departmentIds = new int[count][];
            for (int d = 0; d < count; d++)
            {
                var phrases = Require(DepartmentPhrases, departments[d], "key phrases");
                departmentIds[d] = new int[phrases.Length];
                for (int i = 0; i < phrases.Length; i++)
                {
                    departmentIds[d][i] = FirstKeywordId + texts.Count;
                    texts.Add(phrases[i].Text);
                }
            }
            KeywordTexts = texts.ToArray();
            if (KeywordTexts.Length > 200 || KeywordTexts.Any(t => t.Length > 100)
                || KeywordTexts.Distinct(StringComparer.OrdinalIgnoreCase).Count() != KeywordTexts.Length)
                throw new InvalidOperationException("Key phrases must be unique, fit keywords.name and stay a bounded vocabulary.");

            Candidates = new int[count][][];
            for (int d = 0; d < count; d++)
            {
                var phrases = DepartmentPhrases[departments[d]];
                Candidates[d] = new int[CategoryIds.Length][];
                for (int c = 0; c < CategoryIds.Length; c++)
                {
                    var ids = new List<int>();
                    for (int i = 0; i < phrases.Length; i++) if (phrases[i].Category == c) ids.Add(departmentIds[d][i]);
                    for (int i = 0; i < CommonPhrases.Length; i++) if (CommonPhrases[i].Category == c) ids.Add(FirstKeywordId + i);
                    // A category the department has no phrase for borrows the department's own vocabulary.
                    for (int i = 0; ids.Count < MaxKeywordsPerPrompt && i < phrases.Length; i++)
                        if (!ids.Contains(departmentIds[d][i])) ids.Add(departmentIds[d][i]);
                    Candidates[d][c] = ids.ToArray();
                }
            }

            // Sites: 1-5 as before, then one SharePoint site per department, Teams and a public website.
            var sites = new List<string> { null };
            sites.AddRange(LegacySites);
            for (int d = 0; d < count; d++)
                sites.Add("https://contoso.sharepoint.com/sites/" + Require(SiteSlugs, departments[d], "a SharePoint site"));
            sites.Add(TeamsSite);
            sites.Add(InsightsSite);
            SiteUrls = sites.ToArray();
            int firstDepartmentSite = LegacyCount + 1, teamsSite = SiteUrls.Length - 2, insightsSite = SiteUrls.Length - 1;

            var names = new List<string> { null };
            var nameSites = new List<int> { 0 };
            var nameTypes = new List<int> { 0 };
            for (int i = 1; i <= LegacyCount; i++)
            {
                names.Add(LegacyResourceNames[i - 1]);
                nameSites.Add(i);
                nameTypes.Add(i);
            }
            for (int d = 0; d < count; d++)
            {
                var files = Require(DepartmentFiles, departments[d], "accessed files");
                if (files.Length != DepartmentKinds) throw new InvalidOperationException("Each department needs one resource of every kind.");
                for (int kind = 0; kind < DepartmentKinds; kind++)
                {
                    names.Add(files[kind]);
                    nameSites.Add(kind == Mail ? OutlookSite : kind == Post ? teamsSite : firstDepartmentSite + d);
                    nameTypes.Add(kind == Mail ? EmailType : kind == Post ? MessageType : FileType);
                }
            }
            SalesDepartment = Array.IndexOf(departments, "Sales");
            HrDepartment = Array.IndexOf(departments, "Human Resources");
            int finance = Array.IndexOf(departments, "Finance"), legal = Array.IndexOf(departments, "Legal");
            if (SalesDepartment < 0 || HrDepartment < 0 || finance < 0 || legal < 0)
                throw new InvalidOperationException("The agents and Greek files expect Sales, Human Resources, Finance and Legal.");
            GreekDocx = new int[count];
            GreekXlsx = new int[count];
            foreach (var greek in new[] { Tuple.Create(SalesDepartment, Docx), Tuple.Create(finance, Xlsx), Tuple.Create(legal, Docx) })
            {
                (greek.Item2 == Docx ? GreekDocx : GreekXlsx)[greek.Item1] = names.Count;
                names.Add(GreekFiles[names.Count - (LegacyCount + 1 + count * DepartmentKinds)]);
                nameSites.Add(firstDepartmentSite + greek.Item1);
                nameTypes.Add(FileType);
            }
            FirstExternalPage = names.Count;
            foreach (var page in ExternalPages)
            {
                names.Add(page);
                nameSites.Add(insightsSite);
                nameTypes.Add(WebPageType);
            }
            ResourceNameTexts = names.ToArray();
            ResourceSite = nameSites.ToArray();
            ResourceType = nameTypes.ToArray();
            if (ResourceNameTexts.Length > 128 || SiteUrls.Length > 128
                || ResourceNameTexts.Skip(1).Distinct(StringComparer.Ordinal).Count() != ResourceNameTexts.Length - 1)
                throw new InvalidOperationException("Accessed-resource names must be unique and fit the estimate cache key.");
        }

        public DemoCopilotPersonas(int seed, PromptCategoryConfiguration taxonomy)
        {
            _seed = seed;
            if (taxonomy.Categories.Count != CategoryIds.Length)
                throw new InvalidOperationException("The demo personas cover the default prompt taxonomy; extend them with it.");
            _categories = CategoryIds.Select(id => taxonomy.Categories.SingleOrDefault(c => c.Id == id)
                ?? throw new InvalidOperationException("The prompt taxonomy has no '" + id + "' category.")).ToArray();
        }

        public static int KeywordCount => KeywordTexts.Length;
        public static int ResourceNameCount => ResourceNameTexts.Length - 1;
        public static string ResourceName(int id) => ResourceNameTexts[id];
        public static string ResourceTypeName(int id) => TypeNames[ResourceType[id]];
        public static string ResourceSiteUrl(int id) => SiteUrls[ResourceSite[id]];

        /// <summary>
        /// Writes the prompt key phrases into the shared <c>keywords</c> lookup, after the Teams channel keywords
        /// (<paramref name="existing"/>), which keep ids 1-5.
        /// </summary>
        public static void WriteKeywords(IDemoSink sink, string[] existing)
        {
            if (existing.Length + 1 != FirstKeywordId)
                throw new InvalidOperationException("Prompt key phrases must follow the Teams channel keywords without a gap.");
            if (existing.Any(name => KeywordTexts.Contains(name, StringComparer.OrdinalIgnoreCase)))
                throw new InvalidOperationException("A prompt key phrase duplicates a Teams channel keyword.");
            for (int i = 0; i < KeywordTexts.Length; i++) sink.Write(DemoTables.Keywords, FirstKeywordId + i, KeywordTexts[i]);
        }

        /// <summary>Writes the accessed-resource names, site URLs and types. Ids 1-5 are unchanged from earlier formats.</summary>
        public static void WriteResourceDimensions(IDemoSink sink)
        {
            for (int id = 1; id < ResourceNameTexts.Length; id++) sink.Write(DemoTables.ResourceNames, id, ResourceNameTexts[id]);
            for (int id = 1; id < SiteUrls.Length; id++) sink.Write(DemoTables.ResourceSites, id, SiteUrls[id]);
            for (int id = 1; id < TypeNames.Length; id++) sink.Write(DemoTables.ResourceTypes, id, TypeNames[id]);
        }

        public static void WriteResource(IDemoSink sink, Guid chatId, int resource) =>
            sink.Write(DemoTables.Resources, chatId, resource, ResourceSite[resource], ResourceType[resource]);

        public PromptCategoryDefinition Category(int index) => _categories[index];

        /// <summary>
        /// Picks a prompt's category from what the person's department mostly asks for, the app they are in, the
        /// agent answering, and a personal favourite, with a flat 4% of prompts that fit no category.
        /// </summary>
        public int CategoryIndex(DemoUser user, int dayIndex, int slot, int host, int agent)
        {
            var draw = new Draw(DemoRandom.Value(_seed, user.Id, dayIndex, 3000 + slot));
            if (draw.Next(1000) < OtherPerMille) return OT;
            int favourite = Favourites[user.Department][DemoRandom.Value(_seed, user.Id, 0, 3050) % 3];
            int[] department = Weights[user.Department], hostBoost = HostBoosts[host], agentBoost = AgentBoosts[agent];
            int total = FavouriteWeight;
            for (int c = 0; c < OT; c++) total += department[c] + hostBoost[c] + agentBoost[c];
            int pick = draw.Next(total);
            for (int c = 0; c < OT; c++)
            {
                pick -= department[c] + hostBoost[c] + agentBoost[c] + (c == favourite ? FavouriteWeight : 0);
                if (pick < 0) return c;
            }
            throw new InvalidOperationException("Category weights did not cover the draw.");
        }

        /// <summary>
        /// Picks 1-3 distinct key phrases for an analysed prompt, from its department's vocabulary for the
        /// prompt's category. Half the time the first phrase is the person's usual one for that category, so a
        /// person's top phrases are recognisably theirs. Returns how many of <paramref name="into"/> were filled.
        /// </summary>
        public int Keywords(DemoUser user, int dayIndex, int slot, int category, int[] into)
        {
            var candidates = Candidates[user.Department][category];
            int n = candidates.Length;
            int roll = (int)(DemoRandom.Value(_seed, user.Id, dayIndex, 3100 + slot) % 100);
            int count = Math.Min(n, roll < 35 ? 1 : roll < 75 ? 2 : 3);
            uint v = DemoRandom.Value(_seed, user.Id, dayIndex, 3200 + slot);
            int first = (v & 1) == 0 ? user.Id % n : (int)((v >> 1) % (uint)n);
            into[0] = candidates[first];
            if (count < 2) return 1;
            int second = (first + 1 + (int)((v >> 9) % (uint)(n - 1))) % n;
            into[1] = candidates[second];
            if (count < 3) return 2;
            int skip = (int)((v >> 17) % (uint)(n - 2));
            for (int i = 0; i < n; i++)
            {
                if (i == first || i == second) continue;
                if (skip-- == 0)
                {
                    into[2] = candidates[i];
                    return 3;
                }
            }
            throw new InvalidOperationException("Key phrase pick fell off the candidate list.");
        }

        /// <summary>
        /// A prompt's length and references: look-ups are short and drafting long; in-app prompts carry the open
        /// document as context and often reference a file, Outlook and Teams prompts a link or a person.
        /// </summary>
        public DemoMessageShape Prompt(DemoUser user, int dayIndex, int slot, int host, int agent, int category)
        {
            var draw = new Draw(DemoRandom.Value(_seed, user.Id, dayIndex, 3300 + slot));
            int words = PromptWords[category, 0] + draw.Next(PromptWords[category, 1] - PromptWords[category, 0] + 1);
            var shape = new DemoMessageShape { Words = words, Chars = words * 6 + draw.Next(9) - 4 };
            var extras = new Draw(DemoRandom.Value(_seed, user.Id, dayIndex, 3400 + slot));
            int a = extras.Next(100), l = extras.Next(100), m = extras.Next(100);
            switch (host)
            {
                case 0: // Copilot Chat
                    shape.Attachments = a < 60 ? 0 : a < 90 ? 1 : 2;
                    shape.Links = l < 15 ? 1 : 0;
                    shape.Mentions = m < 70 ? 0 : m < 95 ? 1 : 2;
                    break;
                case 1: // Teams
                    shape.Attachments = a < 10 ? 1 : 0;
                    shape.Links = l < 30 ? 1 : 0;
                    shape.Mentions = m < 60 ? 0 : m < 90 ? 1 : 2;
                    break;
                case 3: // Outlook
                    shape.Attachments = a < 15 ? 1 : 0;
                    shape.Links = l < 30 ? 1 : 0;
                    shape.Mentions = m < 25 ? 1 : 0;
                    break;
                case 2: // Word
                case 4: // Excel
                case 5: // PowerPoint
                    shape.Attachments = a < 60 ? 1 : a < 65 ? 2 : 0;
                    shape.Links = l < 5 ? 1 : 0;
                    shape.Mentions = m < 10 ? 1 : 0;
                    break;
                case 7: // Cowork: delegated tasks usually come with material
                    shape.Attachments = a < 30 ? 0 : a < 80 ? 1 : 2;
                    shape.Links = l < 5 ? 1 : 0;
                    shape.Mentions = m < 10 ? 1 : 0;
                    break;
                default: // OneNote
                    shape.Attachments = a < 10 ? 1 : 0;
                    shape.Links = l < 5 ? 1 : 0;
                    shape.Mentions = m < 10 ? 1 : 0;
                    break;
            }
            shape.Contexts = (host >= 1 && host <= 6 ? 1 : 0) + (agent > 0 ? 1 : 0);
            return shape;
        }

        /// <summary>A response's length and citations: research and analysis answers are longer and cite more.</summary>
        public DemoMessageShape Response(DemoUser user, int dayIndex, int slot, int agent, int category)
        {
            var draw = new Draw(DemoRandom.Value(_seed, user.Id, dayIndex, 3700 + slot));
            int words = ResponseWords[category, 0] + draw.Next(ResponseWords[category, 1] - ResponseWords[category, 0] + 1);
            bool cites = category == IL || category == DR || category == DPS || category == AN;
            return new DemoMessageShape
            {
                Words = words,
                Chars = words * 6,
                Links = draw.Next(cites ? 4 : 2) + (agent > 0 ? 1 : 0),
                Contexts = agent > 0 ? 1 : 0
            };
        }

        /// <summary>
        /// The resources a turn's audit record lists and the credit estimate the importer would store for it.
        /// </summary>
        /// <remarks>
        /// A turn without an agent is priced at 0. The importer also stores its "NoAgent" estimate as JSON on
        /// every such row; the demo writes the 0 but leaves that JSON NULL, because at a million users it would be
        /// tens of millions of identical strings, and the reports read the total, which is the same either way.
        /// </remarks>
        public DemoChatFacts Chat(DemoUser user, int dayIndex, int slot, int host, int agent)
        {
            var picks = new Draw(DemoRandom.Value(_seed, user.Id, dayIndex, 3500 + slot));
            var facts = new DemoChatFacts();
            if (agent == 0)
            {
                HostResources(user, host, ref picks, out facts.FirstResource, out facts.SecondResource);
                return facts;
            }

            bool runtime = false, deep = false;
            int answers = 1;
            if (agent == CoworkAgent)
            {
                // Cowork works through the person's own department material.
                if (picks.Next(100) < 60)
                {
                    int kind = picks.Next(DepartmentKinds);
                    facts.FirstResource = DepartmentResource(user.Department, kind, ref picks);
                    if (picks.Next(100) < 40)
                        facts.SecondResource = DepartmentResource(user.Department,
                            (kind + 1 + picks.Next(DepartmentKinds - 1)) % DepartmentKinds, ref picks);
                }
            }
            else
            {
                var mix = Mixes[agent];
                var composition = new Draw(DemoRandom.Value(_seed, user.Id, dayIndex, 3600 + slot));
                runtime = composition.Next(100) < mix.RuntimePercent;
                int roll = composition.Next(100);
                answers = roll < mix.OneAnswerPercent ? 1 : roll < mix.UpToTwoAnswersPercent ? 2 : 3;
                bool grounded = composition.Next(100) < mix.GroundedPercent;
                deep = composition.Next(100) < mix.DeepPercent;
                if (runtime)
                {
                    answers = 0;
                    deep = false;
                }
                else if (grounded) AgentResources(agent, user.Department, ref picks, out facts.FirstResource, out facts.SecondResource);
                else if (picks.Next(100) < mix.ExternalPercent) facts.FirstResource = ExternalResource(picks.Next(1 + ExternalPages.Length));
            }
            var estimate = Estimate(agent, runtime, answers, deep, facts.FirstResource, facts.SecondResource);
            facts.Estimate = estimate.Key;
            facts.EstimateJson = estimate.Value;
            return facts;
        }

        // Copilot without an agent reads what the app is about: the open document's neighbours in Word, Excel and
        // PowerPoint, the thread in Outlook, the channel in Teams; Copilot Chat ranges across all of them and the
        // web. Free Copilot Chat (no licence) is web-grounded only.
        private static void HostResources(DemoUser user, int host, ref Draw picks, out int first, out int second)
        {
            first = second = 0;
            int department = user.Department, roll = picks.Next(100), extra = picks.Next(100);
            switch (host)
            {
                case 0:
                    if (!user.CopilotLicensed)
                    {
                        if (roll < 25) first = ExternalResource(picks.Next(1 + ExternalPages.Length));
                        return;
                    }
                    if (roll >= 48) return;
                    int kind = picks.Next(DepartmentKinds + 1);
                    first = ChatResource(department, kind, ref picks);
                    if (extra < 15) second = ChatResource(department, (kind + 1 + picks.Next(DepartmentKinds)) % (DepartmentKinds + 1), ref picks);
                    return;
                case 1:
                    if (roll < 40) first = DepartmentResource(department, Post, ref picks);
                    return;
                case 2:
                    if (roll < 60) first = DepartmentResource(department, Docx, ref picks);
                    if (first > 0 && extra < 10) second = DepartmentResource(department, Xlsx, ref picks);
                    return;
                case 3:
                    if (roll < 40) first = DepartmentResource(department, Mail, ref picks);
                    return;
                case 4:
                    if (roll < 70) first = DepartmentResource(department, Xlsx, ref picks);
                    if (first > 0 && extra < 10) second = DepartmentResource(department, Pptx, ref picks);
                    return;
                case 5:
                    if (roll < 65) first = DepartmentResource(department, Pptx, ref picks);
                    if (first > 0 && extra < 10) second = DepartmentResource(department, Xlsx, ref picks);
                    return;
                case 6:
                    if (roll < 25) first = DepartmentResource(department, Docx, ref picks);
                    return;
                default:
                    return;
            }
        }

        // What a grounded agent turn reads: the Knowledge Assistant its curated library and the person's own
        // department documents, the Sales Coach the Sales site, the Onboarding Guide the HR induction material.
        // Every option is tenant content, so the turn carries the grounding charge.
        private static void AgentResources(int agent, int department, ref Draw picks, out int first, out int second)
        {
            int roll = picks.Next(100);
            bool two = picks.Next(100) < 20;
            int a, b, c;
            switch (agent)
            {
                case 1:
                    a = KnowledgeLibrary;
                    b = DeliveryList;
                    c = DepartmentResource(department, Docx, ref picks);
                    first = roll < 50 ? a : roll < 70 ? b : c;
                    second = !two ? 0 : first == a ? b : first == b ? c : a;
                    return;
                case 2:
                    a = DepartmentResource(SalesDepartment, Docx, ref picks);
                    b = DepartmentResource(SalesDepartment, Xlsx, ref picks);
                    first = roll < 50 ? a : b;
                    second = !two ? 0 : first == a ? b : a;
                    return;
                case 4:
                    a = DepartmentResource(HrDepartment, Pptx, ref picks);
                    b = DepartmentResource(HrDepartment, Post, ref picks);
                    c = KnowledgeLibrary;
                    first = roll < 55 ? a : roll < 80 ? b : c;
                    second = !two ? 0 : first == a ? b : first == b ? c : a;
                    return;
                default:
                    first = second = 0;
                    return;
            }
        }

        private static int ChatResource(int department, int kind, ref Draw picks) =>
            kind == DepartmentKinds ? ExternalResource(picks.Next(1 + ExternalPages.Length)) : DepartmentResource(department, kind, ref picks);

        private static int DepartmentResource(int department, int kind, ref Draw picks)
        {
            int greek = kind == Docx ? GreekDocx[department] : kind == Xlsx ? GreekXlsx[department] : 0;
            if (greek > 0 && picks.Next(4) == 0) return greek;
            return LegacyCount + 1 + department * DepartmentKinds + kind;
        }

        // 0 is the original public project page (id 4); 1-3 the public Contoso Insights pages.
        private static int ExternalResource(int variant) => variant == 0 ? ExternalProjectPage : FirstExternalPage + variant - 1;

        private KeyValuePair<int, string> Estimate(int agent, bool runtime, int answers, bool deep, int first, int second)
        {
            int key = ((((agent * 2 + (runtime ? 1 : 0)) * 4 + answers) * 2 + (deep ? 1 : 0)) * 128 + first) * 128 + second;
            KeyValuePair<int, string> cached;
            if (_estimates.TryGetValue(key, out cached)) return cached;

            var record = new EstimateEvent
            {
                Messages = runtime ? null : Messages(answers),
                AccessedResources = new List<EstimateResource>(),
                ModelTransparencyDetails = deep ? new List<EstimateModel> { new EstimateModel { ModelName = DeepReasoningModel } } : null
            };
            foreach (int resource in new[] { first, second })
                if (resource > 0)
                    record.AccessedResources.Add(new EstimateResource { Type = ResourceTypeName(resource), SiteUrl = ResourceSiteUrl(resource) });
            var estimate = CreditEstimation.Analyze(record, AgentKinds[agent]);
            cached = new KeyValuePair<int, string>(estimate.TotalCredits, JsonConvert.SerializeObject(estimate));
            _estimates.Add(key, cached);
            return cached;
        }

        private static List<EstimateMessage> Messages(int answers)
        {
            var messages = new List<EstimateMessage> { new EstimateMessage { Id = "prompt", IsPrompt = true } };
            for (int i = 1; i <= answers; i++) messages.Add(new EstimateMessage { Id = "answer-" + i, IsPrompt = false });
            return messages;
        }

        private static T Require<T>(Dictionary<string, T> table, string department, string what)
        {
            T value;
            if (!table.TryGetValue(department, out value))
                throw new InvalidOperationException("The demo personas define no " + what + " for the '" + department + "' department.");
            return value;
        }

        private static Phrase P(string text, int category) => new Phrase(text, category);

        private struct Phrase
        {
            public readonly string Text;
            public readonly int Category;
            public Phrase(string text, int category)
            {
                Text = text;
                Category = category;
            }
        }

        /// <summary>Splits one hash into mixed-radix digits, so a concern needs one hash rather than one per value.</summary>
        private struct Draw
        {
            private uint _value;
            public Draw(uint value) { _value = value; }
            public int Next(int radix)
            {
                int digit = (int)(_value % (uint)radix);
                _value /= (uint)radix;
                return digit;
            }
        }
    }
}
