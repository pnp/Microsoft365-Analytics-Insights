using App.ControlPanel.Engine;
using Common.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Graph.Copilot.InteractionHistory;

namespace Tests.UnitTests.InstallTests
{
    /// <summary>
    /// Guards the installer's "Test Configuration" against the defect class in issue #329: an import toggle
    /// shipping with no permission check at all.
    /// </summary>
    /// <remarks>
    /// That has now happened three times, and it is invisible in the test output - there is not even a
    /// "skipping ... not being targeted" line for an unverified toggle, so an admin gets a green run and
    /// discovers hours later that nothing was imported. These tests assert over the
    /// <c>[ImportProp]</c> attribute set itself, so adding a toggle without deciding how it is verified fails
    /// the build instead of shipping unnoticed.
    /// </remarks>
    [TestClass]
    public class ImportToggleVerificationCoverageTests
    {
        [TestMethod]
        public void EveryImportToggleDeclaresItsVerificationCoverage()
        {
            var declared = SolutionInstallVerifier.ImportToggleCoverages.Select(c => c.PropertyName).ToList();
            var actual = ImportTaskSettings.GetImportPropertyNames().ToList();

            var missing = actual.Except(declared).ToList();
            var stale = declared.Except(actual).ToList();

            Assert.AreEqual(0, missing.Count,
                $"New [ImportProp] toggle(s) with no verification decision: {string.Join(", ", missing)}. "
                + "Add them to SolutionInstallVerifier.ImportToggleCoverages, either naming the check that "
                + "verifies them or stating why nothing can - otherwise Test Configuration will pass silently "
                + "while the workload is misconfigured.");

            Assert.AreEqual(0, stale.Count,
                $"ImportToggleCoverages names toggle(s) that no longer exist: {string.Join(", ", stale)}.");
        }

        [TestMethod]
        public void ImportToggleCoverageHasNoDuplicates()
        {
            var duplicates = SolutionInstallVerifier.ImportToggleCoverages
                .GroupBy(c => c.PropertyName)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            Assert.AreEqual(0, duplicates.Count,
                $"Duplicate entries in ImportToggleCoverages: {string.Join(", ", duplicates)}. A duplicate would "
                + "let the set-equality check above pass while a toggle is still missing.");
        }

        [TestMethod]
        public void EveryImportToggleCoverageEntryExplainsItself()
        {
            foreach (var coverage in SolutionInstallVerifier.ImportToggleCoverages)
            {
                if (coverage.IsVerified)
                {
                    Assert.IsTrue(string.IsNullOrEmpty(coverage.NotVerifiedReason),
                        $"'{coverage.PropertyName}' claims to be both verified and not verified.");
                }
                else
                {
                    Assert.IsFalse(string.IsNullOrWhiteSpace(coverage.NotVerifiedReason),
                        $"'{coverage.PropertyName}' is not verified but gives no reason. The reason is logged to "
                        + "the admin, so it cannot be blank.");
                }
            }
        }

        [TestMethod]
        public void ImportToggleCoverageRejectsContradictoryOrEmptyDeclarations()
        {
            // Both supplied - contradictory.
            Assert.ThrowsException<ArgumentException>(
                () => new ImportToggleCoverage("Copilot", "some check", "some reason"));

            // Neither supplied - would register a toggle as "covered" while saying nothing about how.
            Assert.ThrowsException<ArgumentException>(() => new ImportToggleCoverage("Copilot", null));
            Assert.ThrowsException<ArgumentException>(() => new ImportToggleCoverage("Copilot", "  ", "  "));

            // No property name.
            Assert.ThrowsException<ArgumentException>(() => new ImportToggleCoverage(" ", "some check"));
        }

        [TestMethod]
        public void TheCopilotToggleIsDeclaredAsVerifiedByTheActivityApiCheck()
        {
            // Gap B in #329: the verifier tested only ActivityLog, so a Copilot-only tenant - a perfectly
            // normal Copilot-focused deployment - was told "audit-data not being targeted" while the importer
            // went on to depend on precisely that API.
            var copilot = SolutionInstallVerifier.ImportToggleCoverages.Single(c => c.PropertyName == nameof(ImportTaskSettings.Copilot));

            Assert.IsTrue(copilot.IsVerified, "The Copilot toggle must be covered by the Activity API check.");
            StringAssert.Contains(copilot.VerifiedBy, "Activity API");
        }

        [TestMethod]
        public void TheCopilotInteractionHistoryToggleIsDeclaredAsVerified()
        {
            // Gap A in #329: nothing referenced CopilotInteractionHistory at all, and it is the ONLY import
            // whose permission the installer does not grant.
            var interactions = SolutionInstallVerifier.ImportToggleCoverages
                .Single(c => c.PropertyName == nameof(ImportTaskSettings.CopilotInteractionHistory));

            Assert.IsTrue(interactions.IsVerified);
            StringAssert.Contains(interactions.VerifiedBy, "AiEnterpriseInteraction.Read.All");
        }

        #region UsesActivityApi - the shared runtime/verifier condition

        [TestMethod]
        public void UsesActivityApiIsTrueForEveryAuditFedImportTheJobActuallyRuns()
        {
            Assert.IsTrue(new ImportTaskSettings { ActivityLog = true }.UsesActivityApi,
                "SharePoint audit is read from the Management Activity API.");

            Assert.IsTrue(new ImportTaskSettings { Copilot = true }.UsesActivityApi,
                "Copilot interactions arrive on the Audit.General feed - this is Gap B in #329, where a "
                + "Copilot-only tenant was told 'audit-data not being targeted'.");
        }

        [TestMethod]
        public void UsesActivityApiExcludesPowerPlatformOnPurpose()
        {
            // ImportPowerPlatform subscribes to Audit.General (see ToActivityApiContentTypesString) but the
            // web-job does not run the activity import for it alone. That IS a bug, but it cannot be fixed by
            // widening this condition: Audit.General also carries Copilot interactions and
            // AuditLogContentDispatcher accepts WORKLOAD_COPILOT unconditionally, so doing so would import
            // Copilot data on a tenant that never opted in. Locked down here so nobody "tidies" it later
            // without dealing with that isolation first.
            Assert.IsFalse(new ImportTaskSettings { ImportPowerPlatform = true }.UsesActivityApi,
                "Widening UsesActivityApi to Power Platform would import Copilot audit data without the "
                + "Copilot toggle. Fix AuditLogContentDispatcher's workload isolation first.");
        }

        [TestMethod]
        public void UsesActivityApiIsFalseWhenNoAuditFedImportIsEnabled()
        {
            Assert.IsFalse(new ImportTaskSettings().UsesActivityApi);

            Assert.IsFalse(
                new ImportTaskSettings
                {
                    GraphTeams = true,
                    GraphUsageReports = true,
                    GraphCopilotUsageReports = true,
                    GraphUsersMetadata = true,
                    CopilotInteractionHistory = true,
                    SentEmails = true,
                    WebTraffic = true,
                    Calls = true,
                }.UsesActivityApi,
                "None of the Graph-only imports touch the Management Activity API.");
        }

        [TestMethod]
        public void UsesActivityApiIsNotSerialisedIntoTheSavedConfig()
        {
            // Derived state, not schema. Both serialisers write public getters by default, so without the
            // JsonIgnore attributes this would land in every saved installer *.json and in
            // sys_configs.ConfigJson - a persisted-schema addition needing a CONFIG_VERSION bump, for a value
            // that is recomputed on load and can never be authoritative.
            var settings = new ImportTaskSettings { Copilot = true };

            var newtonsoft = Newtonsoft.Json.JsonConvert.SerializeObject(settings);
            StringAssert.DoesNotMatch(newtonsoft, new System.Text.RegularExpressions.Regex(nameof(ImportTaskSettings.UsesActivityApi)),
                $"Newtonsoft serialised {nameof(ImportTaskSettings.UsesActivityApi)}: {newtonsoft}");

            var systemTextJson = System.Text.Json.JsonSerializer.Serialize(settings);
            StringAssert.DoesNotMatch(systemTextJson, new System.Text.RegularExpressions.Regex(nameof(ImportTaskSettings.UsesActivityApi)),
                $"System.Text.Json serialised {nameof(ImportTaskSettings.UsesActivityApi)}: {systemTextJson}");
        }

        [TestMethod]
        public void UsesActivityApiIsNotAnImportPropSoSettingsRoundTripIsUnchanged()
        {
            // ToSettingsString / the parsing constructor / Equals all iterate [ImportProp]. A computed
            // property leaking into that set would corrupt the App Service ImportJobSettings value.
            CollectionAssert.DoesNotContain(
                ImportTaskSettings.GetImportPropertyNames().ToList(),
                nameof(ImportTaskSettings.UsesActivityApi));

            var original = new ImportTaskSettings { Copilot = true, GraphTeams = true };
            var roundTripped = new ImportTaskSettings(original.ToSettingsString());

            Assert.IsTrue(original.Equals(roundTripped));
            Assert.AreEqual(original.UsesActivityApi, roundTripped.UsesActivityApi);
            StringAssert.DoesNotMatch(original.ToSettingsString(),
                new System.Text.RegularExpressions.Regex(nameof(ImportTaskSettings.UsesActivityApi)));
        }

        [TestMethod]
        public void EveryToggleThatUsesTheActivityApiSubscribesToAContentTypeItWillActuallyRead()
        {
            // The runtime must never subscribe to a feed it does not read. Asserted against literal expected
            // values rather than recomputing the condition, so a change to either side shows up here.
            var copilotOnly = new ImportTaskSettings { Copilot = true };
            Assert.IsTrue(copilotOnly.UsesActivityApi);
            Assert.AreEqual(ImportTaskSettings.CONTENT_TYPE_AUDIT_GENERAL, copilotOnly.ToActivityApiContentTypesString());

            var auditOnly = new ImportTaskSettings { ActivityLog = true };
            Assert.IsTrue(auditOnly.UsesActivityApi);
            Assert.AreEqual(ImportTaskSettings.CONTENT_TYPE_AUDIT_SHAREPOINT, auditOnly.ToActivityApiContentTypesString());

            var both = new ImportTaskSettings { ActivityLog = true, Copilot = true };
            Assert.IsTrue(both.UsesActivityApi);
            Assert.AreEqual(
                $"{ImportTaskSettings.CONTENT_TYPE_AUDIT_GENERAL};{ImportTaskSettings.CONTENT_TYPE_AUDIT_SHAREPOINT}",
                both.ToActivityApiContentTypesString());
        }

        #endregion

        #region IsImportEnabled

        [TestMethod]
        public void IsImportEnabledReadsTheNamedToggle()
        {
            var settings = new ImportTaskSettings { GraphTeams = true };

            Assert.IsTrue(settings.IsImportEnabled(nameof(ImportTaskSettings.GraphTeams)));
            Assert.IsFalse(settings.IsImportEnabled(nameof(ImportTaskSettings.Calls)));
        }

        [TestMethod]
        public void IsImportEnabledThrowsForAnUnknownToggle()
        {
            // Answering false for an unknown name would let a rename quietly turn the coverage report into a
            // no-op - the same silent-omission failure mode this whole file guards against.
            Assert.ThrowsException<ArgumentException>(
                () => new ImportTaskSettings().IsImportEnabled("NotAToggle"));

            // A real property that is deliberately NOT an [ImportProp] must be rejected too.
            Assert.ThrowsException<ArgumentException>(
                () => new ImportTaskSettings().IsImportEnabled(nameof(ImportTaskSettings.UsesActivityApi)));
        }

        [TestMethod]
        public void EveryDeclaredCoverageNameIsReadableViaIsImportEnabled()
        {
            // ReportUnverifiedEnabledImports calls IsImportEnabled for every declared name, and that throws on
            // an unknown one - so a typo in the table would crash Test Configuration rather than misreport.
            var settings = new ImportTaskSettings();
            var names = new List<string>();

            foreach (var coverage in SolutionInstallVerifier.ImportToggleCoverages)
            {
                settings.IsImportEnabled(coverage.PropertyName);
                names.Add(coverage.PropertyName);
            }

            Assert.AreEqual(SolutionInstallVerifier.ImportToggleCoverages.Count, names.Count);
        }

        #endregion

        #region AiEnterpriseInteraction.Read.All tri-state (Gap A)

        /// <summary>
        /// An app identity that returns a canned JWT (or throws), so the permission check can be exercised
        /// end-to-end without a tenant.
        /// </summary>
        private class FakeAppIdentity : DataUtils.Http.ImportAppIndentityOAuthContext
        {
            private readonly string _jwt;
            private readonly Exception _throw;

            public FakeAppIdentity(string jwt, Exception toThrow = null)
                : base(NullLogger.Instance, "client", "tenant", "secret", null, false)
            {
                _jwt = jwt;
                _throw = toThrow;
            }

            public override string ResourceURL => "https://graph.microsoft.com/.default";

            public override Task<Azure.Core.AccessToken> GetAccessToken()
            {
                if (_throw != null) throw _throw;
                return Task.FromResult(new Azure.Core.AccessToken(_jwt, DateTimeOffset.UtcNow.AddHours(1)));
            }
        }

        /// <summary>Builds an unsigned JWT whose payload carries the given application roles.</summary>
        private static string JwtWithRoles(params string[] roles)
        {
            var payload = new JObject { ["roles"] = new JArray(roles) };
            return "header." + Base64Url(payload.ToString(Newtonsoft.Json.Formatting.None)) + ".signature";
        }

        private static string Base64Url(string value)
        {
            return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private static GraphAiInteractionSourceLoader NewLoader(DataUtils.Http.ImportAppIndentityOAuthContext identity)
        {
            // The HTTP client is unused by the permission check - it reads the token's roles claim only.
            var httpClient = new WebJob.Office365ActivityImporter.Engine.Graph.ManualGraphCallClient(
                new WebJob.Office365ActivityImporter.Engine.GraphAppIndentityOAuthContext(
                    NullLogger.Instance, "client", "tenant", "secret", null, false),
                NullLogger.Instance);

            return new GraphAiInteractionSourceLoader(httpClient, identity, NullLogger.Instance);
        }

        [TestMethod]
        public async Task InteractionReadAccessIsGrantedWhenTheTokenCarriesThePermission()
        {
            var loader = NewLoader(new FakeAppIdentity(JwtWithRoles("AiEnterpriseInteraction.Read.All")));

            Assert.AreEqual(InteractionReadAccess.Granted, await loader.GetInteractionReadAccessAsync());
            Assert.IsTrue(await loader.HasInteractionReadAccessAsync());
        }

        [TestMethod]
        public async Task InteractionReadAccessIsGrantedRegardlessOfClaimCasing()
        {
            // Entra ID's casing of role values is not something to depend on; InteractionReadPermissions is
            // an OrdinalIgnoreCase set, so prove that actually holds.
            var loader = NewLoader(new FakeAppIdentity(JwtWithRoles("aienterpriseinteraction.read.all")));

            Assert.AreEqual(InteractionReadAccess.Granted, await loader.GetInteractionReadAccessAsync());
        }

        [TestMethod]
        public async Task InteractionReadAccessIsNotGrantedWhenTheTokenCarriesOtherPermissions()
        {
            // A readable token that genuinely lacks the role - the ONLY case that should produce the hard
            // "you have not consented to this" error in Test Configuration.
            var loader = NewLoader(new FakeAppIdentity(JwtWithRoles("Reports.Read.All", "User.Read.All")));

            Assert.AreEqual(InteractionReadAccess.NotGranted, await loader.GetInteractionReadAccessAsync());
            Assert.IsFalse(await loader.HasInteractionReadAccessAsync());
        }

        [TestMethod]
        public async Task InteractionReadAccessIsNotGrantedWhenTheTokenParsesButCarriesNoRolesAtAll()
        {
            // A token that decodes cleanly and carries no roles is a DEFINITE absence of consent - the most
            // likely real-world shape of "nobody consented to anything yet" - so it must produce the
            // actionable error, not the indeterminate warning. Distinguishing this from an unreadable token is
            // exactly why GraphTokenPermissions.TryExtract reports parse success separately.
            var loader = NewLoader(new FakeAppIdentity(JwtWithRoles()));

            Assert.AreEqual(InteractionReadAccess.NotGranted, await loader.GetInteractionReadAccessAsync());
            Assert.IsFalse(await loader.HasInteractionReadAccessAsync());
        }

        [TestMethod]
        public void TryExtractSeparatesAnUnreadableTokenFromOneWithNoPermissions()
        {
            Assert.IsTrue(GraphTokenPermissions.TryExtract(JwtWithRoles(), out var none));
            Assert.AreEqual(0, none.Count, "A valid payload with an empty roles array parses to no permissions.");

            Assert.IsTrue(GraphTokenPermissions.TryExtract(JwtWithRoles("Reports.Read.All"), out var some));
            CollectionAssert.AreEquivalent(new[] { "Reports.Read.All" }, some.ToArray());

            foreach (var unreadable in new[] { null, string.Empty, "not-a-jwt", "header..signature" })
            {
                Assert.IsFalse(GraphTokenPermissions.TryExtract(unreadable, out var parsed),
                    $"'{unreadable ?? "<null>"}' should be reported as unparseable.");
                Assert.AreEqual(0, parsed.Count);
            }

            // Extract() keeps its old collapse-to-empty behaviour for callers that only want "does it have X?".
            Assert.AreEqual(0, GraphTokenPermissions.Extract("not-a-jwt").Count);
        }

        [TestMethod]
        public async Task InteractionReadAccessIsUnknownWhenTheTokenCannotBeRead()
        {
            // A malformed token proves nothing - reporting it as a definite missing grant would send an admin
            // to re-consent something they may already hold (issue #329).
            foreach (var unreadable in new[] { "not-a-jwt", string.Empty, "header..signature" })
            {
                var loader = NewLoader(new FakeAppIdentity(unreadable));

                Assert.AreEqual(InteractionReadAccess.Unknown, await loader.GetInteractionReadAccessAsync(),
                    $"Token '{unreadable}' should be indeterminate, not a proven absence.");

                // The importer must still fail closed on it.
                Assert.IsFalse(await loader.HasInteractionReadAccessAsync());
            }
        }

        [TestMethod]
        public async Task InteractionReadAccessIsUnknownWhenTheTokenCannotBeAcquired()
        {
            var loader = NewLoader(new FakeAppIdentity(null, new InvalidOperationException("token endpoint down")));

            Assert.AreEqual(InteractionReadAccess.Unknown, await loader.GetInteractionReadAccessAsync());
            Assert.IsFalse(await loader.HasInteractionReadAccessAsync(),
                "The importer must fail closed when the permission cannot be confirmed.");
        }

        [TestMethod]
        public async Task InteractionReadAccessReportsNoIdentityDistinctlyAndStillFailsOpenForTheImporter()
        {
            // Behaviour preserved from before the tri-state split: the importer carries on and lets the
            // per-user calls report the truth. The verifier must NOT read this as a pass, which is why it is
            // a distinct state rather than folded into Granted.
            var loader = NewLoader(null);

            Assert.AreEqual(InteractionReadAccess.NoIdentityToInspect, await loader.GetInteractionReadAccessAsync());
            Assert.IsTrue(await loader.HasInteractionReadAccessAsync(),
                "The importer's original fail-open behaviour for a null identity must be unchanged.");
        }

        #endregion

        #region The verifier's own reporting decisions

        [TestMethod]
        public void OnlyAProvenAbsenceOfConsentIsReportedAsAnError()
        {
            // The whole point of the tri-state. An indeterminate result reported as an ERROR sends an admin to
            // re-consent a permission they may already hold; reported as nothing at all, a green run would
            // falsely imply the grant was proven.
            var granted = SolutionInstallVerifier.DescribeInteractionReadAccess(InteractionReadAccess.Granted);
            Assert.AreEqual(LogLevel.Information, granted.level);
            StringAssert.Contains(granted.message, "Successfully verified");

            var notGranted = SolutionInstallVerifier.DescribeInteractionReadAccess(InteractionReadAccess.NotGranted);
            Assert.AreEqual(LogLevel.Error, notGranted.level);
            StringAssert.Contains(notGranted.message, "AiEnterpriseInteraction.Read.All");
            StringAssert.Contains(notGranted.message, "installer does NOT grant this permission",
                "The message must say the installer does not grant it - otherwise admins reasonably assume "
                + "the installer's own consent step covered it.");

            foreach (var indeterminate in new[] { InteractionReadAccess.Unknown, InteractionReadAccess.NoIdentityToInspect })
            {
                var result = SolutionInstallVerifier.DescribeInteractionReadAccess(indeterminate);
                Assert.AreEqual(LogLevel.Warning, result.level, $"{indeterminate} must be a warning, not an error or a pass.");
                StringAssert.Contains(result.message, "NOT a failure of the grant itself");
            }
        }

        [TestMethod]
        public void EveryInteractionReadAccessStateHasAReport()
        {
            // A new enum member falling into the default branch would silently be reported as "could not
            // tell", which might be quite wrong.
            foreach (InteractionReadAccess state in Enum.GetValues(typeof(InteractionReadAccess)))
            {
                var (_, message) = SolutionInstallVerifier.DescribeInteractionReadAccess(state);
                Assert.IsFalse(string.IsNullOrWhiteSpace(message), $"{state} produced no message.");
            }
        }

        [TestMethod]
        public void OnlyEnabledUnverifiableTogglesAreReportedToTheAdmin()
        {
            // Nothing enabled -> nothing to say.
            Assert.AreEqual(0, SolutionInstallVerifier.GetUnverifiedEnabledImports(new ImportTaskSettings()).Count);

            // An enabled toggle that IS verified is not reported - the per-check output already covers it.
            Assert.AreEqual(0,
                SolutionInstallVerifier.GetUnverifiedEnabledImports(new ImportTaskSettings { GraphTeams = true }).Count);

            // An enabled toggle with no possible check must be called out.
            var reported = SolutionInstallVerifier.GetUnverifiedEnabledImports(
                new ImportTaskSettings { Calls = true, SentEmails = true, GraphTeams = true });

            CollectionAssert.AreEquivalent(
                new[] { nameof(ImportTaskSettings.Calls), nameof(ImportTaskSettings.SentEmails) },
                reported.Select(c => c.PropertyName).ToArray());

            Assert.IsTrue(reported.All(c => !string.IsNullOrWhiteSpace(c.NotVerifiedReason)));
        }

        [TestMethod]
        public void UnverifiedImportReportingToleratesMissingSettings()
        {
            Assert.AreEqual(0, SolutionInstallVerifier.GetUnverifiedEnabledImports(null).Count);
        }

        [TestMethod]
        public void NoCoverageReasonRecommendsEnablingAnotherWorkloadAsAWorkaround()
        {
            // Guard against re-introducing the advice that caused a review blocker: telling an admin to enable
            // SharePoint audit alongside Power Platform produces ActivityLog + ImportPowerPlatform with
            // Copilot=false, which imports Copilot audit data the tenant never opted in to (because
            // AuditLogContentDispatcher accepts WORKLOAD_COPILOT unconditionally).
            foreach (var coverage in SolutionInstallVerifier.ImportToggleCoverages.Where(c => !c.IsVerified))
            {
                StringAssert.DoesNotMatch(coverage.NotVerifiedReason,
                    new System.Text.RegularExpressions.Regex(@"[Ee]nable the .* import alongside"),
                    $"'{coverage.PropertyName}' tells the admin to enable another workload as a workaround.");
            }
        }

        #endregion
    }
}
