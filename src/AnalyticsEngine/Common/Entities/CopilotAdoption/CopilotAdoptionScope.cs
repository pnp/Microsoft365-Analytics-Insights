using System;
using System.Collections.Generic;
using System.Linq;

namespace Common.Entities.CopilotAdoption
{
    /// <summary>
    /// Which slice of the tenant a Copilot adoption result describes.
    ///
    /// <para>Today the only axis is the email domain - i.e. which of the organisations sharing this
    /// tenant is being looked at. It is a type rather than a bare string so that adding a second axis
    /// later does not mean changing the signature of every endpoint again.</para>
    /// </summary>
    public class CopilotAdoptionScope
    {
        private CopilotAdoptionScope(string emailDomain)
        {
            EmailDomain = emailDomain;
        }

        /// <summary>The whole tenant - no narrowing at all.</summary>
        public static readonly CopilotAdoptionScope WholeTenant = new CopilotAdoptionScope(null);

        /// <summary>
        /// The domain every figure is narrowed to, already normalised by
        /// <see cref="CopilotAdoptionEmailDomain.Normalise"/>, or <c>null</c> for the whole tenant.
        /// </summary>
        public string EmailDomain { get; }

        /// <summary>True when this scope actually narrows anything.</summary>
        public bool IsNarrowed => !string.IsNullOrWhiteSpace(EmailDomain);

        /// <summary>
        /// Builds a scope from a caller-supplied domain. Anything blank, or that does not normalise to
        /// a domain, yields <see cref="WholeTenant"/> rather than an empty result set - a hand-edited
        /// URL should show the tenant, not an empty dashboard that reads as zero adoption.
        /// </summary>
        public static CopilotAdoptionScope ForEmailDomain(string emailDomain)
        {
            var normalised = CopilotAdoptionEmailDomain.Normalise(emailDomain);

            return string.IsNullOrWhiteSpace(normalised)
                ? WholeTenant
                : new CopilotAdoptionScope(normalised);
        }
    }

    /// <summary>
    /// Names of the sections that stay tenant-wide when a result is narrowed to one email domain.
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
    }

    /// <summary>
    /// Narrows a completed analysis to one email domain.
    ///
    /// <para><b>Why it works this way.</b> The expensive part of this feature is the SQL, and it is
    /// cached once per (window, licence override). Re-running it per domain would multiply the load on
    /// a database the importer is already sharing, for a report whose slowest queries are measured in
    /// minutes. So the domain filter is applied <i>after</i> the fact: the per-user rows are filtered in
    /// memory and the summary is rebuilt from them with the same
    /// <see cref="CopilotAdoptionService.FinaliseSummary"/> the tenant-wide summary was built with.
    /// One scoring implementation, so a domain view can never disagree with the tenant view about how a
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

            var domain = scope.EmailDomain;
            var tenant = analysis.Summary ?? new CopilotAdoptionSummary();

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

                LicensedUsers = Narrow(analysis.LicensedUsers, u => u.EmailDomain, domain),
                Opportunities = Narrow(analysis.Opportunities, o => o.EmailDomain, domain),
                // The cap is applied to the tenant-wide ranking, so a narrowed list inherits it.
                OpportunitiesCapped = analysis.OpportunitiesCapped,
                CoworkReadiness = Narrow(analysis.CoworkReadiness, c => c.EmailDomain, domain),
                CoworkSignals = Narrow(analysis.CoworkSignals, s => s.EmailDomain, domain),
                UnlicensedUsers = Narrow(analysis.UnlicensedUsers, u => u.EmailDomain, domain),
            };
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
                SeatLicenceTypes = cloneSeatLicenceTypes
                    ? (tenant.SeatLicenceTypes ?? new List<LicenceTypeClassification>())
                        .Select(l => l.Clone())
                        .ToList()
                    : tenant.SeatLicenceTypes,

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

            return scoped;
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

            scoped.UnscopedSections = new List<string>
            {
                CopilotAdoptionUnscopedSections.UsageByApp,
                CopilotAdoptionUnscopedSections.TopResourceTypes,
                CopilotAdoptionUnscopedSections.WeeklyTrend,
                CopilotAdoptionUnscopedSections.Agents,
                CopilotAdoptionUnscopedSections.PurchasedSeats,
                CopilotAdoptionUnscopedSections.CoworkCredits,
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

        private static List<T> Narrow<T>(List<T> rows, Func<T, string> domainOf, string domain)
        {
            if (rows == null) return new List<T>();

            return rows
                .Where(r => string.Equals(
                    CopilotAdoptionEmailDomain.Label(domainOf(r)), domain, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
    }
}
