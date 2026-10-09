using Common.Entities.UserFilters;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Common.Entities.CopilotAdoption
{
    /// <summary>
    /// Which slice of the tenant a Copilot adoption result describes.
    ///
    /// <para>Two axes, both optional and combined with AND: the email domain - which of the
    /// organisations sharing this tenant is being looked at - and a <see cref="CompiledUserFilter"/> over
    /// the people themselves: their Entra ID attributes and the custom organisations an admin has
    /// defined. It is a type rather than a pair of parameters so that adding a third axis later does not
    /// mean changing the signature of every endpoint again.</para>
    /// </summary>
    public class CopilotAdoptionScope
    {
        private CopilotAdoptionScope(
            string emailDomain,
            CompiledUserFilter userFilter,
            CompiledUserFilter restriction = null,
            GlobalFilterEcho globalFilter = null,
            string globalFilterDescription = null)
        {
            EmailDomain = emailDomain;
            UserFilter = userFilter;
            Restriction = restriction;
            GlobalFilter = restriction == null ? null : globalFilter;
            GlobalFilterDescription = restriction == null ? null : globalFilterDescription;
        }

        /// <summary>The whole tenant - no narrowing at all.</summary>
        public static readonly CopilotAdoptionScope WholeTenant = new CopilotAdoptionScope(null, null);

        /// <summary>
        /// The domain every figure is narrowed to, already normalised by
        /// <see cref="CopilotAdoptionEmailDomain.Normalise"/>, or <c>null</c> for the whole tenant.
        /// </summary>
        public string EmailDomain { get; }

        /// <summary>The user filter every figure is narrowed by, or <c>null</c> for none.</summary>
        public CompiledUserFilter UserFilter { get; }

        /// <summary>
        /// The administrator's global filter, resolved for the reader, that every figure is ALSO narrowed by -
        /// or <c>null</c> when none applies. Kept apart from <see cref="UserFilter"/> because the page shows
        /// the two apart: the administrator's conditions locked, the reader's own editable.
        /// </summary>
        public CompiledUserFilter Restriction { get; }

        /// <summary>The global filter as the page shows it, set whenever <see cref="Restriction"/> is.</summary>
        public GlobalFilterEcho GlobalFilter { get; }

        /// <summary>The global filter in plain English, for the Excel workbook.</summary>
        public string GlobalFilterDescription { get; }

        /// <summary>True when this scope actually narrows anything.</summary>
        public bool IsNarrowed => !string.IsNullOrWhiteSpace(EmailDomain) || UserFilter != null || Restriction != null;

        /// <summary>
        /// Builds a scope from a caller-supplied domain. Anything blank, or that does not normalise to
        /// a domain, yields <see cref="WholeTenant"/> rather than an empty result set - a hand-edited
        /// URL should show the tenant, not an empty dashboard that reads as zero adoption.
        /// </summary>
        public static CopilotAdoptionScope ForEmailDomain(string emailDomain)
        {
            return Create(emailDomain, null);
        }

        /// <summary>
        /// Builds a scope from a caller-supplied domain and an already-compiled user filter, either of
        /// which may be absent. A person is in scope when they match both.
        /// </summary>
        public static CopilotAdoptionScope Create(string emailDomain, CompiledUserFilter userFilter)
        {
            return Create(emailDomain, userFilter, null, null, null);
        }

        /// <summary>
        /// As <see cref="Create(string, CompiledUserFilter)"/>, ANDed with the administrator's global filter
        /// when one applies. A person is in scope when they match all three.
        /// </summary>
        public static CopilotAdoptionScope Create(
            string emailDomain,
            CompiledUserFilter userFilter,
            CompiledUserFilter restriction,
            GlobalFilterEcho globalFilter,
            string globalFilterDescription)
        {
            var normalised = CopilotAdoptionEmailDomain.Normalise(emailDomain);

            if (string.IsNullOrWhiteSpace(normalised) && userFilter == null && restriction == null) return WholeTenant;

            return new CopilotAdoptionScope(
                string.IsNullOrWhiteSpace(normalised) ? null : normalised,
                userFilter,
                restriction,
                globalFilter,
                globalFilterDescription);
        }

        /// <summary>
        /// Whether one row's person is in scope. The domain is the row's own, derived by the analysis;
        /// the user filter and the global filter are evaluated by user id against the directory snapshot
        /// they were compiled with.
        /// </summary>
        public bool Includes(int userId, string rowEmailDomain)
        {
            if (!string.IsNullOrWhiteSpace(EmailDomain)
                && !string.Equals(CopilotAdoptionEmailDomain.Label(rowEmailDomain), EmailDomain, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return (UserFilter == null || UserFilter.Matches(userId)) && (Restriction == null || Restriction.Matches(userId));
        }

        /// <summary>
        /// Whether a person the analysis holds no row for would be in scope, where the scope can tell. A user
        /// filter or global filter carries the directory, which knows the person's attributes and email domain
        /// alike; an email domain on its own has nothing to look a missing person up in, so that answers
        /// <c>null</c>.
        /// </summary>
        public bool? IncludesFromDirectory(int userId)
        {
            var directory = UserFilter ?? Restriction;
            if (directory == null) return null;
            if (UserFilter != null && !UserFilter.Matches(userId)) return false;
            if (Restriction != null && !Restriction.Matches(userId)) return false;

            return string.IsNullOrWhiteSpace(EmailDomain)
                || string.Equals(
                    CopilotAdoptionEmailDomain.Label(directory.EmailDomainOf(userId)),
                    EmailDomain,
                    StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Names of the sections that stay tenant-wide when a result is narrowed to one email domain or by
    /// a user filter.
    ///
    /// <para>Compile-time constants, published in
    /// <see cref="CopilotAdoptionSummary.UnscopedSections"/> so the UI can badge each one. Never
    /// anything derived from tenant data.</para>
    /// </summary>
    public static class CopilotAdoptionUnscopedSections
    {
        /// <summary>Copilot use by host app. A <c>GROUP BY app_host</c> with no user identity in the result.</summary>
        public const string UsageByApp = "usageByApp";

        /// <summary>How the audit log typed the resources Copilot referenced. Aggregated in SQL with no user identity.</summary>
        public const string TopResourceTypes = "topResourceTypes";

        /// <summary>The six-month weekly trend. Aggregated per week in SQL, so there are no rows to filter.</summary>
        public const string WeeklyTrend = "weeklyTrend";

        /// <summary>
        /// The agent estate. Agents are tenant-level objects with a usage total across everybody, so the
        /// inventory, its health verdicts and its per-agent totals cannot be attributed to one domain.
        /// Left whole rather than half-filtered, so the tab is internally consistent.
        /// </summary>
        public const string Agents = "agents";

        /// <summary>
        /// Purchased and unassigned Copilot seats. A tenant-level purchase from Graph
        /// <c>subscribedSkus</c>; seats are not bought per domain.
        /// </summary>
        public const string PurchasedSeats = "purchasedSeats";

        /// <summary>The Cowork credit balance. Entitlement and consumption are tenant-level.</summary>
        public const string CoworkCredits = "coworkCredits";

        /// <summary>
        /// Microsoft's own tenant figures from its Copilot usage report (#642) - the prompt totals and the
        /// report date and period they describe. Microsoft publishes them for the tenant as a whole, with no
        /// per-person detail to narrow.
        /// </summary>
        public const string MicrosoftReport = "microsoftReport";
    }

    /// <summary>
    /// Narrows a completed analysis to a <see cref="CopilotAdoptionScope"/>: one email domain, a user
    /// filter over Entra ID attributes and custom organisations, or both.
    ///
    /// <para><b>Why it works this way.</b> The expensive part of this feature is the SQL, and it is
    /// cached once per (window, licence override). Re-running it per domain or per filter would multiply
    /// the load on a database the importer is already sharing, for a report whose slowest queries are
    /// measured in minutes. So the scope is applied <i>after</i> the fact: the per-user rows are filtered
    /// in memory and the summary is rebuilt from them with the same
    /// <see cref="CopilotAdoptionService.FinaliseSummary"/> the tenant-wide summary was built with.
    /// One scoring implementation, so a filtered view can never disagree with the tenant view about how a
    /// number is calculated.</para>
    ///
    /// <para><b>What cannot be narrowed.</b> Several sections come from aggregate queries that return no
    /// user identity - usage by app, the weekly trend, the agent inventory - and a few describe
    /// tenant-level facts that are not per-domain at all, like how many seats were purchased. Those are
    /// carried across unchanged and <i>named</i> in
    /// <see cref="CopilotAdoptionSummary.UnscopedSections"/>. Dropping them would lose real information;
    /// silently leaving them next to narrowed figures would invite the reader to compare two different
    /// populations. Naming them is the only honest option.</para>
    /// </summary>
    public static class CopilotAdoptionScopeFilter
    {
        /// <summary>
        /// The row collections narrowed to the scope, sharing the caller's summary.
        /// </summary>
        /// <remarks>
        /// For the list, paging and export endpoints, which read the rows and the data-source warnings
        /// but never the aggregate figures. Deliberately cheaper than <see cref="Apply"/>: it does not
        /// rebuild the summary, so a user paging through a table does not re-score the population on
        /// every request.
        /// </remarks>
        public static CopilotAdoptionAnalysis FilterRows(CopilotAdoptionAnalysis analysis, CopilotAdoptionScope scope)
        {
            if (analysis == null) throw new ArgumentNullException(nameof(analysis));
            if (scope == null || !scope.IsNarrowed) return analysis;

            var tenant = analysis.Summary ?? new CopilotAdoptionSummary();
            var licensedUsers = Narrow(analysis.LicensedUsers, u => u.UserId, u => u.EmailDomain, scope);
            var coworkSignals = Narrow(analysis.CoworkSignals, s => s.UserId, s => s.EmailDomain, scope);

            return new CopilotAdoptionAnalysis
            {
                // A seeded summary rather than the tenant one, because these endpoints publish
                // summary.Warnings alongside the rows and flatten them onto every exported CSV row.
                // The tenant list contains POPULATION warnings raised by scoring - "N licensed users
                // were scored from Microsoft's usage report" - whose N is the tenant's. Stamping that
                // onto a one-organisation export states a number about a population the file does not
                // contain. The data-source warnings, which are true of any subset, are kept.
                //
                // No scoring runs here, so the licence types can be shared rather than cloned.
                Summary = SeedScopedSummary(tenant, scope, cloneSeatLicenceTypes: false),
                Sql = analysis.Sql,
                Agents = analysis.Agents,
                // Unlike the inventory, a person: narrowed like every other per-person list, so a filtered
                // view lists its own heaviest agent users. The cap was tenant-wide, so a slice inherits it.
                AgentUsers = Narrow(analysis.AgentUsers, u => u.UserId, u => u.EmailDomain, scope),
                AgentUsersCapped = analysis.AgentUsersCapped,

                LicensedUsers = licensedUsers,
                // Like the opportunity cap below: the licensed-user query's cap was applied to the whole
                // tenant, so a slice of it is just as short of the people past it.
                LicensedUsersCapped = analysis.LicensedUsersCapped,
                Opportunities = Narrow(analysis.Opportunities, o => o.UserId, o => o.EmailDomain, scope),
                // The cap is applied to the tenant-wide ranking, so a narrowed list inherits it.
                OpportunitiesCapped = analysis.OpportunitiesCapped,
                // Withheld when no Cowork signal in the slice has a licensed-user row, which is exactly when
                // its summary says the tab cannot be scored (FinaliseCowork, through the same test): every
                // row here was scored against a fluency of 0 it does not have. The row and export endpoints
                // read this list directly, so it has to agree with the summary here, not only there.
                CoworkReadiness = CopilotAdoptionScoring.CoworkCanBeScored(coworkSignals, licensedUsers)
                    ? Narrow(analysis.CoworkReadiness, c => c.UserId, c => c.EmailDomain, scope)
                    : new List<CoworkReadinessRow>(),
                CoworkSignals = coworkSignals,
                // Only a complete assessment can vouch for an empty slice. When the Cowork query hit its
                // row cap, the people a slice selects may simply have been cut off, and "0 candidates"
                // would be a claim about people nobody assessed - so that case is flagged separately and
                // explained rather than shown as zero or as a missing import.
                CoworkAssessedForWholePopulation = tenant.CoworkReadinessAvailable && !CoworkCapped(analysis, tenant),
                CoworkAssessmentCapped = tenant.CoworkReadinessAvailable && CoworkCapped(analysis, tenant),
                UnlicensedUsers = Narrow(analysis.UnlicensedUsers, u => u.UserId, u => u.EmailDomain, scope),
                // Resolved against the whole tenant, not the slice: a report in this view may well have a
                // manager outside it, and that manager's own Copilot use is no less known for it (#641).
                ManagerDirectory = CopilotAdoptionManagerModelling.InheritedDirectory(analysis),
                SeatHolderTimeSavedRows = NarrowSeatTimeRows(analysis.SeatHolderTimeSavedRows, licensedUsers),
                SeatHolderTimeSavedAssessed = analysis.SeatHolderTimeSavedAssessed,
            };
        }

        private static List<SeatHolderTimeSavedUserRow> NarrowSeatTimeRows(
            IEnumerable<SeatHolderTimeSavedUserRow> rows, IEnumerable<LicensedUserAdoptionRow> licensedUsers)
        {
            if (rows == null) return new List<SeatHolderTimeSavedUserRow>();
            var userIds = new HashSet<int>((licensedUsers ?? new List<LicensedUserAdoptionRow>()).Select(u => u.UserId));
            return rows.Where(r => userIds.Contains(r.UserId)).ToList();
        }

        /// <summary>
        /// A summary carrying only the facts that survive narrowing: when the analysis ran, over what
        /// period, which imports supplied data, and which of them failed.
        /// </summary>
        /// <remarks>
        /// Built fresh rather than copied from the tenant summary and overwritten. If a figure is ever
        /// added to the summary and not to this method, the narrowed view shows it as empty - visibly
        /// missing. Copy-then-overwrite fails the other way, and would show one subsidiary a
        /// tenant-wide number under a heading naming that subsidiary. Of the two ways to be wrong,
        /// only one is detectable by the person reading it.
        /// </remarks>
        private static CopilotAdoptionSummary SeedScopedSummary(
            CopilotAdoptionSummary tenant, CopilotAdoptionScope scope, bool cloneSeatLicenceTypes)
        {
            return new CopilotAdoptionSummary
            {
                ScopedEmailDomain = scope.EmailDomain,

                // Echoed, like the domain, so the page states which population it is showing rather
                // than assuming the request it sent is the one that was applied.
                UserFilter = scope.UserFilter?.ToEcho(scope.Restriction),
                UserFilterDescription = scope.UserFilter?.DescribeInEnglish(),

                // The administrator's global filter, echoed apart from the reader's own: the page shows
                // it locked, and the workbook states it on its cover.
                GlobalFilter = scope.GlobalFilter,
                GlobalFilterDescription = scope.GlobalFilterDescription,

                // The tenant-wide seat count, so a narrowed page can say "312 of 4,210 licence holders"
                // - the proportion is what tells a reader whether the slice is representative.
                UnscopedLicensedUsers = tenant.LicensedUsers,

                // When the analysis ran, over what period, and with which licence types counted as a
                // seat. Identical for every slice of it.
                GeneratedUtc = tenant.GeneratedUtc,
                WindowDays = tenant.WindowDays,
                FromUtc = tenant.FromUtc,
                ToUtc = tenant.ToUtc,
                Options = tenant.Options,

                // Cloned when a scoring pass is going to follow, because that pass writes this
                // domain's idle-seat count onto each SKU and sharing the list would write it straight
                // into the cached tenant-wide analysis every other caller is reading.
                SeatLicenceTypes = SeatLicenceTypesFor(tenant, scope, cloneSeatLicenceTypes),

                // Which imports supplied data, which queries failed, and how long each step took. All
                // statements about the analysis run, not about the population.
                DataSources = tenant.DataSources,
                Diagnostics = tenant.Diagnostics,
                FiguresIncomplete = tenant.FiguresIncomplete,
                IncompleteReasons = new List<string>(tenant.IncompleteReasons ?? new List<string>()),

                // Only the data-source warnings. Any population warning is re-raised by the scoring
                // pass, with this domain's own numbers in it.
                Warnings = new List<string>(tenant.SourceWarnings ?? new List<string>()),
                SourceWarnings = new List<string>(tenant.SourceWarnings ?? new List<string>()),
                WarningDetails = (tenant.SourceWarningDetails ?? new List<CopilotAdoptionWarningDetail>())
                    .Select(d => d.Clone())
                    .ToList(),
                SourceWarningDetails = (tenant.SourceWarningDetails ?? new List<CopilotAdoptionWarningDetail>())
                    .Select(d => d.Clone())
                    .ToList(),
            };
        }

        /// <summary>
        /// The whole analysis narrowed to the scope, with the summary rebuilt from the narrowed rows.
        /// </summary>
        /// <param name="analysis">The cached tenant-wide analysis. Never modified.</param>
        /// <param name="scope">The slice wanted. <see cref="CopilotAdoptionScope.WholeTenant"/> returns <paramref name="analysis"/> unchanged.</param>
        /// <param name="finaliseSummary">
        /// The scoring pass to rebuild the summary with - in practice
        /// <see cref="CopilotAdoptionService.FinaliseSummary"/>. Injected rather than called directly so
        /// this stays a pure transformation with no database dependency, and so it can be tested with a
        /// stub.
        /// </param>
        public static CopilotAdoptionAnalysis Apply(
            CopilotAdoptionAnalysis analysis,
            CopilotAdoptionScope scope,
            Action<CopilotAdoptionAnalysis> finaliseSummary)
        {
            if (analysis == null) throw new ArgumentNullException(nameof(analysis));
            if (finaliseSummary == null) throw new ArgumentNullException(nameof(finaliseSummary));
            if (scope == null || !scope.IsNarrowed) return analysis;

            var tenant = analysis.Summary ?? new CopilotAdoptionSummary();
            var scoped = FilterRows(analysis, scope);

            // The licence types must be CLONED for this path: the scoring pass below writes this
            // domain's idle-seat count onto each SKU.
            scoped.Summary = SeedScopedSummary(tenant, scope, cloneSeatLicenceTypes: true);

            finaliseSummary(scoped);

            ScopeSeatLicenceTypes(scoped);
            CarryUnscopedSections(tenant, scoped.Summary);
            WarnOfSeatHoldersNotAnalysed(analysis, scope, scoped.Summary);

            return scoped;
        }

        /// <summary>
        /// Says how many licence holders this view selects that the analysis never covered, when its
        /// licensed-user query stopped at its row cap.
        /// </summary>
        /// <remarks>
        /// <para>The cap follows internal user id, so it cuts the newest user records first, and a custom
        /// organisation - a new joiners' programme, a subsidiary onboarded last year - can be made largely or
        /// entirely of them. Narrowed from the capped rows alone, such a view reads as nobody, beside a
        /// tenant-wide warning that cannot say how much of this one view is missing. So the people the cap
        /// left out are counted against the scope too, through the user filter's directory.</para>
        /// <para>Only a scope with a user filter can: an email domain on its own has no directory to look a
        /// person without a row up in, and the tenant-wide warning already covers that case as before.</para>
        /// </remarks>
        private static void WarnOfSeatHoldersNotAnalysed(
            CopilotAdoptionAnalysis analysis, CopilotAdoptionScope scope, CopilotAdoptionSummary scoped)
        {
            var notAnalysed = analysis.LicensedUsersNotAnalysed;
            if (notAnalysed == null || notAnalysed.Count == 0 || (scope.UserFilter == null && scope.Restriction == null)) return;

            var inScope = 0;
            foreach (var userId in notAnalysed)
            {
                if (scope.IncludesFromDirectory(userId) == true) inScope++;
            }

            if (inScope == 0) return;

            CopilotAdoptionWarnings.Add(
                scoped,
                CopilotAdoptionWarningKeys.ScopedLicensedUsersNotAnalysed,
                new Dictionary<string, object>
                {
                    { "count", inScope },
                    { "maxUsers", scoped.Options?.MaxLicensedUsersScored ?? CopilotAdoptionOptions.Default.MaxLicensedUsersScored },
                });
        }

        /// <summary>
        /// The licence types a narrowed summary lists.
        /// </summary>
        /// <remarks>
        /// Under the administrator's global filter, only the Copilot seat types. No other type's assignment
        /// count can be narrowed - a user row carries its Copilot seats only (see
        /// <see cref="ScopeSeatLicenceTypes"/>) - and its assigned, purchased and unassigned counts all describe
        /// the whole tenant, which is what that filter keeps a restricted reader from reading. Narrowed by an
        /// email domain or by the reader's own filter, every type is listed, the others tenant-wide, as before.
        /// </remarks>
        private static List<LicenceTypeClassification> SeatLicenceTypesFor(
            CopilotAdoptionSummary tenant, CopilotAdoptionScope scope, bool clone)
        {
            if (scope.Restriction == null && !clone) return tenant.SeatLicenceTypes;

            IEnumerable<LicenceTypeClassification> listed = tenant.SeatLicenceTypes ?? new List<LicenceTypeClassification>();
            if (scope.Restriction != null) listed = listed.Where(l => l.IsCopilotSeat);

            return clone ? listed.Select(l => l.Clone()).ToList() : listed.ToList();
        }

        /// <summary>
        /// Brings the per-SKU <c>assigned</c> count into line with the <c>idle</c> count the scoring
        /// pass just wrote, so the seat table reads as one population.
        /// </summary>
        /// <remarks>
        /// <c>AssignedUsers</c> arrives from the licence-type query as a tenant-wide total, while
        /// <c>AssignedIdleUsers</c> is recomputed from whichever users were scored. Left alone, a
        /// narrowed view would show one subsidiary's idle seats against the whole tenant's assigned
        /// seats and make the idle share look tiny. Purchased and unassigned units are deliberately
        /// NOT touched - seats are bought by the tenant, not by a domain, which is why
        /// <see cref="CopilotAdoptionUnscopedSections.PurchasedSeats"/> is published.
        /// <para><b>Copilot SKUs only.</b> A user row's <c>SeatLicenceTypeIds</c> comes from a query
        /// restricted to the Copilot seat ids, so counting against a non-Copilot licence type would
        /// always produce zero and replace a true tenant-wide assignment count with a fabricated
        /// zero - next to a tenant-wide purchased figure, which is an internally impossible row.</para>
        /// <para>Run after the scoring pass rather than before it, so the "Graph reports N purchased
        /// but M assigned" warning that pass raises keeps quoting the tenant-wide figures it is
        /// actually a statement about.</para>
        /// </remarks>
        private static void ScopeSeatLicenceTypes(CopilotAdoptionAnalysis scoped)
        {
            var users = scoped.LicensedUsers ?? new List<LicensedUserAdoptionRow>();
            var copilotSkus = (scoped.Summary.SeatLicenceTypes ?? new List<LicenceTypeClassification>())
                .Where(l => l.IsCopilotSeat)
                .ToList();

            if (copilotSkus.Count == 0) return;

            var assigned = new int[copilotSkus.Count];

            // One pass over the users rather than one pass per SKU. At the 200k-user scale this report
            // is designed for, the nested form is a scan per licence type on every scoped request.
            foreach (var user in users)
            {
                for (var i = 0; i < copilotSkus.Count; i++)
                {
                    if (CopilotAdoptionService.UserHasLicence(user, copilotSkus[i])) assigned[i]++;
                }
            }

            for (var i = 0; i < copilotSkus.Count; i++)
            {
                copilotSkus[i].AssignedUsers = assigned[i];
            }
        }

        /// <summary>
        /// Copies the sections that have no per-domain meaning and records their names, so the UI can
        /// label each one as tenant-wide.
        /// </summary>
        private static void CarryUnscopedSections(CopilotAdoptionSummary tenant, CopilotAdoptionSummary scoped)
        {
            scoped.UsageByApp = tenant.UsageByApp;
            scoped.TopResourceTypes = tenant.TopResourceTypes;
            scoped.WeeklyTrend = tenant.WeeklyTrend;
            scoped.WeeklyVolumeTrend = tenant.WeeklyVolumeTrend;
            scoped.CoworkCreditPosition = tenant.CoworkCreditPosition;

            // Taken whole rather than half-narrowed. The scoring pass recomputes the two agent-USER
            // counts from the narrowed population, but an agent's own totals, health verdict and
            // inventory position are tenant-level and cannot be attributed to a domain. A card reading
            // "12 agents, 40 agent users" where only the second number is about this domain is worse
            // than one that is wholly tenant-wide and says so.
            scoped.Agents = tenant.Agents;

            // Microsoft's tenant totals, carried whole and named like the rest: there is no per-person
            // detail in Microsoft's summary to narrow them with.
            scoped.MicrosoftReportDate = tenant.MicrosoftReportDate;
            scoped.MicrosoftReportPeriodDays = tenant.MicrosoftReportPeriodDays;
            scoped.MicrosoftReportVersion = tenant.MicrosoftReportVersion;
            scoped.MicrosoftReportPromptsSubmitted = tenant.MicrosoftReportPromptsSubmitted;
            scoped.MicrosoftReportAveragePromptsPerActiveUser = tenant.MicrosoftReportAveragePromptsPerActiveUser;

            scoped.UnscopedSections = new List<string>
            {
                CopilotAdoptionUnscopedSections.UsageByApp,
                CopilotAdoptionUnscopedSections.TopResourceTypes,
                CopilotAdoptionUnscopedSections.WeeklyTrend,
                CopilotAdoptionUnscopedSections.Agents,
                CopilotAdoptionUnscopedSections.PurchasedSeats,
                CopilotAdoptionUnscopedSections.CoworkCredits,
                CopilotAdoptionUnscopedSections.MicrosoftReport,
            };

            // The unlicensed population's app breakdown is the same kind of SQL aggregate as the
            // licensed one, and the scoring pass leaves it empty because there is nothing per-user to
            // rebuild it from. Everything else on that object - the user count, the interaction total,
            // the habit distribution - HAS been narrowed, so only this one field is carried across.
            if (scoped.Unlicensed != null && tenant.Unlicensed != null)
            {
                scoped.Unlicensed.UsageByApp = tenant.Unlicensed.UsageByApp;
                scoped.Unlicensed.Truncated = tenant.Unlicensed.Truncated;
            }
        }

        private static bool CoworkCapped(CopilotAdoptionAnalysis analysis, CopilotAdoptionSummary tenant)
        {
            return (analysis.CoworkSignals?.Count ?? 0) >= (tenant.Options?.MaxCoworkUsersScored ?? int.MaxValue);
        }

        private static List<T> Narrow<T>(List<T> rows, Func<T, int> userIdOf, Func<T, string> domainOf, CopilotAdoptionScope scope)
        {
            if (rows == null) return new List<T>();

            return rows
                .Where(r => scope.Includes(userIdOf(r), domainOf(r)))
                .ToList();
        }
    }
}
