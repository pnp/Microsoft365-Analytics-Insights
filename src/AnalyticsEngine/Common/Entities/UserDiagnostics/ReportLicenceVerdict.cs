using Common.Entities.CopilotAdoption;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Common.Entities.UserDiagnostics
{
    /// <summary>
    /// Why a report counts one user as licensed or unlicensed, recomputed now from the report's own rules and the data
    /// stored now. It is not a replay of a report someone saw earlier: if the data changed since, the answer can differ,
    /// and the model says so.
    /// </summary>
    public class ReportLicenceVerdictModel
    {
        /// <summary>One of <see cref="ReportVerdictReports"/>.</summary>
        [JsonProperty("report")]
        public string Report { get; set; }

        /// <summary><c>licensed</c> or <c>unlicensed</c>.</summary>
        [JsonProperty("verdict")]
        public string Verdict { get; set; }

        /// <summary>
        /// <c>currentAssignments</c> (the licences stored now, whatever the period) or <c>licenceHistory</c> (licences
        /// held at any point in the period).
        /// </summary>
        [JsonProperty("basis")]
        public string Basis { get; set; }

        [JsonProperty("fromUtc")]
        public DateTime? FromUtc { get; set; }

        [JsonProperty("toExclusiveUtc")]
        public DateTime? ToExclusiveUtc { get; set; }

        [JsonProperty("windowDays")]
        public int? WindowDays { get; set; }

        [JsonProperty("usesExplicitDates")]
        public bool UsesExplicitDates { get; set; }

        [JsonProperty("historyTrackingBeganUtc")]
        public DateTime? HistoryTrackingBeganUtc { get; set; }

        /// <summary><c>automatic</c> (the classification rules) or <c>reportOverride</c> (a what-if seat selection).</summary>
        [JsonProperty("seatSelection")]
        public string SeatSelection { get; set; }

        /// <summary>The licence types the report counted, with why each was or was not a seat.</summary>
        [JsonProperty("countedLicenceTypes")]
        public List<VerdictLicenceTypeModel> CountedLicenceTypes { get; set; } = new List<VerdictLicenceTypeModel>();

        /// <summary>The rows that make the user licensed. Empty is the "unlicensed" verdict.</summary>
        [JsonProperty("decidingRows")]
        public List<VerdictEvidenceRowModel> DecidingRows { get; set; } = new List<VerdictEvidenceRowModel>();

        /// <summary>Report-specific caveats, as stable keys (see <see cref="ReportVerdictNotes"/>).</summary>
        [JsonProperty("notes")]
        public List<string> Notes { get; set; } = new List<string>();

        /// <summary>Always true: the trace re-evaluates the report's rule against the data stored now.</summary>
        [JsonProperty("isRecomputation")]
        public bool IsRecomputation { get; set; } = true;

        [JsonProperty("recomputedAtUtc")]
        public DateTime RecomputedAtUtc { get; set; }

        /// <summary>The query the trace ran, with literals, so an administrator can run it in SSMS.</summary>
        [JsonProperty("sql")]
        public string Sql { get; set; }
    }

    public class VerdictLicenceTypeModel
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("skuPartNumber")]
        public string SkuPartNumber { get; set; }

        [JsonProperty("classification")]
        public ClassificationExplanationModel Classification { get; set; }
    }

    public class VerdictEvidenceRowModel
    {
        [JsonProperty("licenceTypeId")]
        public int LicenceTypeId { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("skuPartNumber")]
        public string SkuPartNumber { get; set; }

        /// <summary>For history rows: when a licence refresh first observed the holding.</summary>
        [JsonProperty("validFromUtc")]
        public DateTime? ValidFromUtc { get; set; }

        [JsonProperty("validToUtc")]
        public DateTime? ValidToUtc { get; set; }

        [JsonProperty("heldBeforeTrackingBegan")]
        public bool HeldBeforeTrackingBegan { get; set; }
    }

    public static class ReportVerdictReports
    {
        public const string CopilotAdoption = "copilotAdoption";
        public const string LicenceActivity = "licenceActivity";
    }

    public static class ReportVerdicts
    {
        public const string Licensed = "licensed";
        public const string Unlicensed = "unlicensed";
    }

    public static class ReportVerdictBases
    {
        public const string CurrentAssignments = "currentAssignments";
        public const string LicenceHistory = "licenceHistory";
    }

    /// <summary>Stable caveat keys; the portal writes the sentences.</summary>
    public static class ReportVerdictNotes
    {
        /// <summary>No licence type counts as a Copilot seat, so nobody can be licensed in this report.</summary>
        public const string NoSeatLicenceTypes = "noSeatLicenceTypes";

        /// <summary>The period starts before licence history began; earlier holdings are unknown, held-at-start rows count.</summary>
        public const string HistoryPartialForPeriod = "historyPartialForPeriod";

        /// <summary>A past period, but the database has no licence history, so today's assignments were used.</summary>
        public const string HistoryUnavailableForPeriod = "historyUnavailableForPeriod";

        /// <summary>A rolling window uses today's assignments: someone licensed last week but not today counts as unlicensed.</summary>
        public const string RollingWindowUsesCurrentAssignments = "rollingWindowUsesCurrentAssignments";

        /// <summary>Licence Activity always uses today's assignments, whatever period is selected.</summary>
        public const string LicenceActivityUsesCurrentAssignments = "licenceActivityUsesCurrentAssignments";

        /// <summary>Guest accounts are left out of the unlicensed and opportunity lists, so this person never appears there.</summary>
        public const string GuestExcludedFromUnlicensedLists = "guestExcludedFromUnlicensedLists";

        /// <summary>The selected licence ids match no licence type that exists now.</summary>
        public const string NoMatchingLicenceTypes = "noMatchingLicenceTypes";
    }

    /// <summary>A licence row returned by <see cref="CopilotAdoptionSql.UserSeatEvidenceSql"/>.</summary>
    public sealed class SeatEvidenceRow
    {
        public int LicenceTypeId { get; set; }
        public DateTime? ValidFromUtc { get; set; }
        public DateTime? ValidToUtc { get; set; }
        public byte? FromSource { get; set; }
    }

    /// <summary>The database reads a verdict needs. SQL in production, an in-memory fake in tests.</summary>
    public interface IReportVerdictQuery
    {
        Task<List<LicenceTypeRow>> LicenceTypesAsync();

        /// <summary><see cref="CopilotAdoptionSql.LicenceHistoryStartSql"/>: null when history is not recorded.</summary>
        Task<DateTime?> LicenceHistoryStartAsync();

        Task<List<SeatEvidenceRow>> SeatEvidenceAsync(int userId, IReadOnlyList<int> licenceTypeIds, bool useLicenceHistory,
            DateTime fromUtc, DateTime toExclusiveUtc, DateTime? historyStartUtc);
    }

    /// <summary>
    /// Builds <see cref="ReportLicenceVerdictModel"/> with the same seat resolution, history decision and predicate the
    /// reports use - <see cref="CopilotLicenceClassifier.ResolveSeatLicenceTypeIds"/> and
    /// <see cref="CopilotAdoptionSql.UserSeatEvidenceSql"/> - so the explanation cannot disagree with the report.
    /// </summary>
    public static class ReportLicenceVerdictBuilder
    {
        public static async Task<ReportLicenceVerdictModel> ForCopilotAdoptionAsync(
            IReportVerdictQuery query,
            int userId,
            string userPrincipalName,
            CopilotAdoptionDateRange range,
            IReadOnlyCollection<int> seatOverride,
            DateTime nowUtc)
        {
            if (query == null) throw new ArgumentNullException(nameof(query));
            if (range == null) throw new ArgumentNullException(nameof(range));

            var licenceTypes = await query.LicenceTypesAsync() ?? new List<LicenceTypeRow>();
            var classifications = CopilotLicenceClassifier.Classify(licenceTypes);
            var seatIds = CopilotLicenceClassifier.ResolveSeatLicenceTypeIds(licenceTypes, seatOverride);
            var overridden = seatOverride != null && seatOverride.Count > 0;
            if (overridden)
            {
                CopilotLicenceClassifier.ApplyOverride(classifications, seatIds);
            }

            var model = new ReportLicenceVerdictModel
            {
                Report = ReportVerdictReports.CopilotAdoption,
                FromUtc = Utc(range.FromUtc),
                ToExclusiveUtc = Utc(range.ToExclusiveUtc),
                WindowDays = range.WindowDays,
                UsesExplicitDates = range.UsesExplicitDates,
                SeatSelection = overridden ? "reportOverride" : "automatic",
                RecomputedAtUtc = Utc(nowUtc),
                CountedLicenceTypes = classifications
                    .Where(c => c.IsCopilotSeat)
                    .Select(c => new VerdictLicenceTypeModel
                    {
                        Id = c.Id,
                        Name = c.Name,
                        SkuPartNumber = c.SkuPartNumber,
                        Classification = new ClassificationExplanationModel
                        {
                            IsCopilotSeat = c.IsCopilotSeat,
                            Reason = c.ClassificationReason,
                            MatchedRule = c.MatchedRule,
                        },
                    })
                    .ToList(),
            };

            if (seatIds.Count == 0)
            {
                model.Verdict = ReportVerdicts.Unlicensed;
                model.Basis = ReportVerdictBases.CurrentAssignments;
                model.Notes.Add(ReportVerdictNotes.NoSeatLicenceTypes);
                AddGuestNote(model, userPrincipalName);
                return model;
            }

            // Exactly the report's decision: history only for an explicit past range, and only when recorded.
            DateTime? historyStart = null;
            var useHistory = false;
            if (range.UsesExplicitDates)
            {
                historyStart = await query.LicenceHistoryStartAsync();
                useHistory = historyStart.HasValue;
                model.HistoryTrackingBeganUtc = Utc(historyStart);
                if (!useHistory)
                {
                    model.Notes.Add(ReportVerdictNotes.HistoryUnavailableForPeriod);
                }
                else if (range.FromUtc < historyStart.Value)
                {
                    model.Notes.Add(ReportVerdictNotes.HistoryPartialForPeriod);
                }
            }
            else
            {
                model.Notes.Add(ReportVerdictNotes.RollingWindowUsesCurrentAssignments);
            }

            model.Basis = useHistory ? ReportVerdictBases.LicenceHistory : ReportVerdictBases.CurrentAssignments;
            var rows = await query.SeatEvidenceAsync(userId, seatIds, useHistory, range.FromUtc, range.ToExclusiveUtc, historyStart)
                ?? new List<SeatEvidenceRow>();
            model.DecidingRows = ToEvidence(rows, licenceTypes, historyStart);
            model.Verdict = model.DecidingRows.Count > 0 ? ReportVerdicts.Licensed : ReportVerdicts.Unlicensed;
            model.Sql = CopilotAdoptionSql.ForDisplay(CopilotAdoptionSql.UserSeatEvidenceSql(seatIds, useHistory), Parameters(userId, useHistory, range, historyStart));
            AddGuestNote(model, userPrincipalName);
            return model;
        }

        /// <summary>
        /// Licence Activity counts a person under a licence when SQL holds that assignment now - the period selects
        /// activity, not licences - so the verdict is the current lookups for the selected licence types.
        /// </summary>
        public static async Task<ReportLicenceVerdictModel> ForLicenceActivityAsync(
            IReportVerdictQuery query,
            int userId,
            IReadOnlyCollection<int> licenceTypeIds,
            DateTime nowUtc)
        {
            if (query == null) throw new ArgumentNullException(nameof(query));

            var licenceTypes = await query.LicenceTypesAsync() ?? new List<LicenceTypeRow>();
            var requested = new HashSet<int>(licenceTypeIds ?? new List<int>());
            var selected = licenceTypes.Where(t => requested.Count == 0 || requested.Contains(t.Id)).ToList();

            var model = new ReportLicenceVerdictModel
            {
                Report = ReportVerdictReports.LicenceActivity,
                Basis = ReportVerdictBases.CurrentAssignments,
                SeatSelection = requested.Count == 0 ? "automatic" : "reportOverride",
                RecomputedAtUtc = Utc(nowUtc),
                CountedLicenceTypes = selected.Select(t => new VerdictLicenceTypeModel
                {
                    Id = t.Id,
                    Name = t.Name,
                    SkuPartNumber = t.SkuPartNumber,
                    Classification = LiveLicenceComparer.ToModel(CopilotLicenceClassifier.Explain(t.SkuPartNumber, t.Name)),
                }).ToList(),
            };
            model.Notes.Add(ReportVerdictNotes.LicenceActivityUsesCurrentAssignments);

            if (selected.Count == 0)
            {
                model.Verdict = ReportVerdicts.Unlicensed;
                model.Notes.Add(ReportVerdictNotes.NoMatchingLicenceTypes);
                return model;
            }

            var ids = selected.Select(t => t.Id).ToList();
            var rows = await query.SeatEvidenceAsync(userId, ids, false, nowUtc, nowUtc, null) ?? new List<SeatEvidenceRow>();
            model.DecidingRows = ToEvidence(rows, licenceTypes, null);
            model.Verdict = model.DecidingRows.Count > 0 ? ReportVerdicts.Licensed : ReportVerdicts.Unlicensed;
            model.Sql = CopilotAdoptionSql.ForDisplay(CopilotAdoptionSql.UserSeatEvidenceSql(ids, false),
                new Dictionary<string, object> { { "@userId", userId } });
            return model;
        }

        private static Dictionary<string, object> Parameters(int userId, bool useHistory, CopilotAdoptionDateRange range, DateTime? historyStart)
        {
            var parameters = new Dictionary<string, object> { { "@userId", userId } };
            if (useHistory)
            {
                parameters["@from"] = range.FromUtc;
                parameters["@toExclusive"] = range.ToExclusiveUtc;
                parameters["@historyStart"] = historyStart.Value;
            }
            return parameters;
        }

        private static List<VerdictEvidenceRowModel> ToEvidence(IEnumerable<SeatEvidenceRow> rows, IEnumerable<LicenceTypeRow> licenceTypes, DateTime? historyStart)
        {
            var byId = licenceTypes.GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First());
            return rows.Select(r =>
            {
                byId.TryGetValue(r.LicenceTypeId, out var type);
                return new VerdictEvidenceRowModel
                {
                    LicenceTypeId = r.LicenceTypeId,
                    Name = type?.Name,
                    SkuPartNumber = type?.SkuPartNumber,
                    ValidFromUtc = Utc(r.ValidFromUtc),
                    ValidToUtc = Utc(r.ValidToUtc),
                    // The report's own rule, so the explanation never contradicts the verdict it explains.
                    HeldBeforeTrackingBegan = Common.Entities.Licences.LicenceHistorySources.HeldBeforeTrackingBegan(r.FromSource, r.ValidFromUtc, historyStart),
                };
            }).ToList();
        }

        private static void AddGuestNote(ReportLicenceVerdictModel model, string userPrincipalName)
        {
            if (!string.IsNullOrEmpty(userPrincipalName) && userPrincipalName.IndexOf("#EXT#@", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                model.Notes.Add(ReportVerdictNotes.GuestExcludedFromUnlicensedLists);
            }
        }

        private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

        private static DateTime? Utc(DateTime? value) => value.HasValue ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc) : (DateTime?)null;
    }
}
