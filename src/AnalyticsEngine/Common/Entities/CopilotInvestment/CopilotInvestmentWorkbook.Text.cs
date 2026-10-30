using Common.Entities.Localisation;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Common.Entities.CopilotInvestment
{
    public static partial class CopilotInvestmentWorkbook
    {
        /// <summary>
        /// The workbook's words in one language: catalog text, product codes as the page labels them, and numbers in the
        /// language's conventions - as the page writes them, including Spanish leaving four-digit numbers ungrouped.
        /// </summary>
        internal sealed class Words
        {
            private const string Prefix = "copilotInvestment.workbook.";

            internal const string TrustLevel = "copilotInvestment.exec.trust.level.";
            internal const string TrustArea = "copilotInvestment.exec.trust.area.";
            internal const string TrustReason = "copilotInvestment.trust.";
            internal const string Scenario = "copilotInvestment.scenario.";
            internal const string Signal = "copilotInvestment.signal.";
            internal const string Flag = "copilotInvestment.flag.";
            internal const string Band = "copilotInvestment.band.";
            internal const string Impact = "copilotInvestment.impact.";
            internal const string Provenance = "copilotInvestment.provenance.";
            internal const string Tone = Prefix + "tone.";
            internal const string Effect = Prefix + "effect.";
            internal const string SeatBasis = Prefix + "seatBasis.";
            internal const string SignalSource = Prefix + "signalSource.";
            internal const string Reclaim = Prefix + "reclaim.";
            internal const string PeriodStatus = "copilotInvestment.quality.period.";

            private readonly PortalCatalog _text;

            internal Words(PortalLanguage language)
            {
                Language = language;
                _text = PortalCatalog.For(CatalogModule, language);
                Culture = PortalLanguages.Culture(language);
            }

            internal PortalLanguage Language { get; }

            internal CultureInfo Culture { get; }

            /// <summary>Workbook wording, by its key below <c>copilotInvestment.workbook.</c>.</summary>
            internal string W(string key, params (string Name, string Value)[] values) => _text.Text(Prefix + key, values);

            /// <summary>Wording the page itself uses, by its full key - the same words on the screen and in the workbook.</summary>
            internal string T(string key, params (string Name, string Value)[] values) => _text.Text(key, values);

            internal bool Has(string key) => _text.Has(key);

            /// <summary>A product code as the page labels it, or the code itself for one this build has no label for.</summary>
            internal string Label(string prefix, string code) =>
                string.IsNullOrEmpty(code) ? code : _text.Has(prefix + code) ? _text.Text(prefix + code) : code;

            internal string Num(double? value, int decimals = 2)
            {
                if (!value.HasValue || double.IsNaN(value.Value) || double.IsInfinity(value.Value)) return "-";
                var text = value.Value.ToString(decimals > 0 ? "#,0." + new string('#', decimals) : "#,0", Culture);
                // Spanish does not group a four-digit number ("2190 €", as the page and the RAE write it).
                if (Language == PortalLanguage.Spanish && Math.Abs(value.Value) < 10000)
                    text = text.Replace(Culture.NumberFormat.NumberGroupSeparator, string.Empty);
                return text;
            }

            internal string Count(double? value) => Num(value, 0);

            internal string Pct(double? value, int decimals = 1)
            {
                if (!value.HasValue || double.IsNaN(value.Value) || double.IsInfinity(value.Value)) return "-";
                var number = Num(value.Value * 100, decimals);
                return Language == PortalLanguage.Spanish ? number + "\u00a0%" : number + "%";
            }
        }

        /// <summary>The page's label for each assumption (the portal's <c>FIELD_LABEL_KEYS</c>).</summary>
        private static readonly IReadOnlyDictionary<string, string> FieldLabelKeys = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [CopilotInvestmentFields.Currency] = "copilotInvestment.field.currency",
            [CopilotInvestmentFields.SeatPricePerMonth] = "copilotInvestment.field.seatPricePerMonth",
            [CopilotInvestmentFields.CreditPrice] = "copilotInvestment.field.creditPrice",
            [CopilotInvestmentFields.StudioCreditPrice] = "copilotInvestment.field.studioCreditPrice",
            [CopilotInvestmentFields.HourlyCost] = "copilotInvestment.field.hourlyCost",
            [CopilotInvestmentFields.MinutesConservative] = "copilotInvestment.field.minutesConservative",
            [CopilotInvestmentFields.MinutesBase] = "copilotInvestment.field.minutesBase",
            [CopilotInvestmentFields.MinutesUpside] = "copilotInvestment.field.minutesUpside",
            [CopilotInvestmentFields.RealisationConservative] = "copilotInvestment.field.realisationConservative",
            [CopilotInvestmentFields.RealisationBase] = "copilotInvestment.field.realisationBase",
            [CopilotInvestmentFields.RealisationUpside] = "copilotInvestment.field.realisationUpside",
            [CopilotInvestmentFields.Note] = "copilotInvestment.field.note",
        };

        /// <summary>The page's label for each credit quadrant (the portal's <c>QUADRANT_LABEL_KEYS</c>).</summary>
        private static readonly IReadOnlyDictionary<string, string> QuadrantLabelKeys = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [CopilotInvestmentCalculator.QuadrantKeys.HighSpendStrongEvidence] = "copilotInvestment.quadrants.highSpendHighEvidence",
            [CopilotInvestmentCalculator.QuadrantKeys.HighSpendThinEvidence] = "copilotInvestment.quadrants.highSpendLowEvidence",
            [CopilotInvestmentCalculator.QuadrantKeys.LowSpendStrongEvidence] = "copilotInvestment.quadrants.lowSpendHighEvidence",
            [CopilotInvestmentCalculator.QuadrantKeys.LowSpendThinEvidence] = "copilotInvestment.quadrants.lowSpendLowEvidence",
        };

        private static string FieldLabel(string field, Words words) =>
            field != null && FieldLabelKeys.TryGetValue(field, out var key) ? words.T(key) : field;

        private static string QuadrantLabel(string quadrant, Words words) =>
            quadrant != null && QuadrantLabelKeys.TryGetValue(quadrant, out var key) ? words.T(key) : quadrant;

        private static string ActionTitle(string code, Words words)
        {
            var key = "copilotInvestment.action." + code + ".title";
            return words.Has(key) ? words.T(key) : code;
        }

        /// <summary>One finding as a sentence, for an analyst: counts as labelled figures, so no wording depends on a number.</summary>
        internal static string FindingText(CopilotInvestmentFinding finding, string currency, PortalLanguage language = PortalLanguage.English) =>
            FindingText(finding, currency, new Words(language));

        private static string FindingText(CopilotInvestmentFinding finding, string currency, Words words)
        {
            double? V(string key) => finding.Values.TryGetValue(key, out var v) ? v : null;
            switch (finding.Code)
            {
                case CopilotInvestmentFindingCodes.RunRate:
                    return words.W("finding.runRate", ("holders", words.Count(V("holders"))), ("monthly", words.Num(V("monthly"))),
                        ("currency", currency), ("costPerActive", words.Num(V("costPerActive"))));
                case CopilotInvestmentFindingCodes.ValueRange:
                    return words.W("finding.valueRange", ("low", words.Count(V("low"))), ("high", words.Count(V("high"))), ("base", words.Count(V("base"))),
                        ("cost", words.Count(V("cost"))), ("currency", currency), ("roiLow", words.Num(V("roiLow"))), ("roiHigh", words.Num(V("roiHigh"))),
                        ("roiBase", words.Num(V("roiBase"))));
                case CopilotInvestmentFindingCodes.BreakEven:
                    return words.W("finding.breakEven", ("minutes", words.Num(V("minutes"))), ("assumed", words.Num(V("assumed"))), ("margin", words.Pct(V("margin"))),
                        ("days", words.Num(V("daysPerMonth"))), ("share", words.Pct(V("shareAtOrAbove"))));
                case CopilotInvestmentFindingCodes.IdleSeats:
                    return words.W("finding.idleSeats", ("idle", words.Count(V("idle"))), ("share", words.Pct(V("share"))), ("reclaimable", words.Count(V("reclaimable"))),
                        ("monthly", words.Num(V("monthly"))), ("currency", currency), ("review", words.Count(V("review"))), ("tooNew", words.Count(V("tooNew"))));
                case CopilotInvestmentFindingCodes.EvidenceConcentration:
                    return words.W("finding.evidenceConcentration", ("topFraction", words.Pct(V("topFraction"))), ("topShare", words.Pct(V("topShare"))),
                        ("gini", words.Num(V("gini"))));
                case CopilotInvestmentFindingCodes.CreditConcentration:
                    return words.W("finding.creditConcentration", ("consumers", words.Count(V("consumers"))), ("topFraction", words.Pct(V("topFraction"))),
                        ("topShare", words.Pct(V("topShare"))));
                case CopilotInvestmentFindingCodes.HighSpendThinEvidence:
                    return words.W("finding.highSpendThinEvidence", ("people", words.Count(V("people"))), ("credits", words.Num(V("credits"))));
                case CopilotInvestmentFindingCodes.ExpandCandidates:
                    return words.W("finding.expandCandidates", ("people", words.Count(V("people"))), ("monthly", words.Num(V("monthly"))), ("currency", currency));
                case CopilotInvestmentFindingCodes.LimitBlocked:
                    return words.W("finding.limitBlocked", ("people", words.Count(V("people"))), ("evidenced", words.Count(V("evidenced"))));
                case CopilotInvestmentFindingCodes.AssumptionsMissing:
                    return words.W("finding.assumptionsMissing", ("missing", words.Count(V("missing"))));
                case CopilotInvestmentFindingCodes.BalancesUnverified:
                    var balances = words.W("finding.balancesUnverified", ("credits", words.Num(V("credits"))), ("consumers", words.Count(V("consumers"))));
                    return V("roiIfMonthly").HasValue ? balances + " " + words.W("finding.balancesStressFloor", ("roi", words.Num(V("roiIfMonthly")))) : balances;
                case CopilotInvestmentFindingCodes.StudioTrend:
                    return V("prior").HasValue
                        ? words.W("finding.studioTrend", ("credits", words.Num(V("credits"))), ("prior", words.Num(V("prior"))), ("change", words.Pct(V("change"))))
                        : words.W("finding.studioTrendNotComparable", ("credits", words.Num(V("credits"))));
                case CopilotInvestmentFindingCodes.Trajectory:
                    return words.W("finding.trajectory", ("first", words.Count(V("first"))), ("last", words.Count(V("last"))), ("weeks", words.Count(V("weeks"))),
                        ("change", words.Pct(V("change"))), ("share", words.Pct(V("share"))));
                case CopilotInvestmentFindingCodes.NoMatchedHolders:
                    return words.W("finding.noMatchedHolders", ("otherPeriod", words.Count(V("otherPeriod"))), ("notAnalysed", words.Count(V("notAnalysed"))));
                case CopilotInvestmentFindingCodes.NoSeats:
                    return words.W("finding.noSeats");
                default:
                    return finding.Code;
            }
        }

        private static string ActionText(CopilotInvestmentAction action, Words words)
        {
            switch (action.Code)
            {
                case CopilotInvestmentActionCodes.ReclaimIdle:
                    var reclaim = words.W("action.reclaimIdle");
                    return action.RoiBaseAfter.HasValue ? reclaim + " " + words.W("action.reclaimIdle.roi", ("roi", words.Num(action.RoiBaseAfter) + MultipleUnit)) : reclaim;
                case CopilotInvestmentActionCodes.ReviewIdle: return words.W("action.reviewIdle");
                case CopilotInvestmentActionCodes.EnableBelowBreakEven: return words.W("action.enableBelowBreakEven", ("days", words.Num(action.Metric, 1)));
                case CopilotInvestmentActionCodes.EnableLightEvidence: return words.W("action.enableLightEvidence");
                case CopilotInvestmentActionCodes.ExpandEvidenced: return words.W("action.expandEvidenced", ("days", words.Num(action.Metric, 1)));
                case CopilotInvestmentActionCodes.ExpandRecommended: return words.W("action.expandRecommended");
                case CopilotInvestmentActionCodes.ReviewHighSpendThinEvidence:
                    var review = words.W("action.reviewHighSpendThinEvidence", ("credits", words.Num(action.Credits)));
                    return action.AssumesMonthlyBalances ? review + " " + words.W("action.reviewHighSpendThinEvidence.monthly") : review;
                case CopilotInvestmentActionCodes.RaiseLimitEvidenced: return words.W("action.raiseLimitEvidenced");
                case CopilotInvestmentActionCodes.LicenceMeteredHeavy: return words.W("action.licenceMeteredHeavy");
                case CopilotInvestmentActionCodes.ImproveCoverage: return words.W("action.improveCoverage");
                case CopilotInvestmentActionCodes.SetAssumptions: return words.W("action.setAssumptions");
                default: return action.Code;
            }
        }

        /// <summary>
        /// One confidence caveat in the page's own words (<c>copilotInvestment.trust.*</c>). Its figures are formatted by
        /// name, as the page formats them: shares as percentages, everything else as counts or credits.
        /// </summary>
        private static string TrustText(CopilotInvestmentTrustReason reason, Words words)
        {
            var key = Words.TrustReason + reason.Code;
            if (!words.Has(key)) return reason.Code;
            var values = reason.Values.Select(pair =>
                (pair.Key, pair.Key == "coverage" || pair.Key == "share" ? words.Pct(pair.Value, 0)
                    : pair.Key == "credits" ? words.Num(pair.Value) : words.Count(pair.Value))).ToArray();
            return words.T(key, values);
        }
    }
}
