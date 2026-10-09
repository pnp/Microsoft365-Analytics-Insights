using System;
using System.Collections.Generic;
using System.Linq;

namespace Common.Entities.CopilotAdoption
{
    /// <summary>
    /// Versioned Microsoft guidance links attached to the action the product recommends.
    /// </summary>
    /// <remarks>
    /// <para>Each link carries a stable <see cref="AdoptionGuidanceLink.TitleKey"/>, one per resource. The
    /// portal translates the title through that key and falls back to <see cref="AdoptionGuidanceLink.Title"/>
    /// only for a key its build does not know (<c>serverAuthoredText.test.ts</c> keeps the two in step).
    /// The workbook and CSV exports keep the English title.</para>
    /// <para><c>.github/scripts/Test-CopilotAdoptionGuidanceLinks.ps1</c> parses the <c>Link(...)</c> calls
    /// below from this source file, so keep each one on a single line with literal string arguments.</para>
    /// </remarks>
    public static class CopilotAdoptionGuidanceCatalogue
    {
        public const string Version = "2026.10.07";

        public const string UnlicensedActionCode = "unlicensed";

        public const int ExpectedLinkCount = 17;

        // The 2026 Work Trend Index is linked by its own article URL. The shorter
        // /worklab/work-trend-index/2026/annual-report path renders the WorkLab home page for any
        // unknown path with HTTP 200, so neither the link checker nor a reader could tell it from a
        // broken link (#643).
        private static readonly IReadOnlyList<AdoptionGuidanceLink> Links = new[]
        {
            Link(CopilotAdoptionScoring.AdoptionActionCodes.Reclaim, "licenseAllocationGuide", "License allocation guidance", "https://aka.ms/Copilot/LicenseAllocationGuide", "Microsoft 365 Copilot license allocation guidance for rapid value", "admin"),
            Link(CopilotAdoptionScoring.AdoptionActionCodes.Reclaim, "copilotAcademy", "Copilot Academy", "https://aka.ms/copilot-academy", "Viva Learning", "admin"),

            Link(CopilotAdoptionScoring.AdoptionActionCodes.Reengage, "scenarioLibrary", "Microsoft Scenario Library", "https://aka.ms/ScenarioLibrary", "Microsoft Scenario Library – Microsoft Adoption", "departmentLead"),

            Link(CopilotAdoptionScoring.AdoptionActionCodes.Coach, "copilotAcademy", "Copilot Academy", "https://aka.ms/copilot-academy", "Viva Learning", "enablementOwner"),
            Link(CopilotAdoptionScoring.AdoptionActionCodes.Coach, "scenarioLibrary", "Microsoft Scenario Library", "https://aka.ms/ScenarioLibrary", "Microsoft Scenario Library – Microsoft Adoption", "enablementOwner"),
            Link(CopilotAdoptionScoring.AdoptionActionCodes.Coach, "workTrendIndex2026", "2026 Work Trend Index", "https://www.microsoft.com/en-us/worklab/work-trend-index/agents-human-agency-and-the-opportunity-for-every-organization", "2026 Work Trend Index report: Agents, human agency, and opportunity", "enablementOwner"),

            Link(CopilotAdoptionScoring.AdoptionActionCodes.Broaden, "scenarioLibrary", "Microsoft Scenario Library", "https://aka.ms/ScenarioLibrary", "Microsoft Scenario Library – Microsoft Adoption", "departmentLead"),

            Link(CopilotAdoptionScoring.AdoptionActionCodes.Grow, "successKit", "Copilot Success Kit", "https://aka.ms/Copilot/SuccessKit", "Copilot Success Kit – Microsoft Adoption", "departmentLead"),
            Link(CopilotAdoptionScoring.AdoptionActionCodes.Grow, "scenarioLibrary", "Microsoft Scenario Library", "https://aka.ms/ScenarioLibrary", "Microsoft Scenario Library – Microsoft Adoption", "departmentLead"),

            Link(CopilotAdoptionScoring.AdoptionActionCodes.Advocate, "essentialGuide", "The essential guide to Microsoft 365 Copilot adoption", "https://adoption.microsoft.com/en-us/copilot/essential-guide/", "The essential guide to Microsoft 365 Copilot adoption – Microsoft Adoption", "enablementOwner"),
            Link(CopilotAdoptionScoring.AdoptionActionCodes.Advocate, "aiCouncil", "Creating an AI Council", "https://adoption.microsoft.com/en-us/copilot/ai-council/", "Creating an AI Council – Microsoft Adoption", "enablementOwner"),
            Link(CopilotAdoptionScoring.AdoptionActionCodes.Advocate, "workTrendIndex2026", "2026 Work Trend Index", "https://www.microsoft.com/en-us/worklab/work-trend-index/agents-human-agency-and-the-opportunity-for-every-organization", "2026 Work Trend Index report: Agents, human agency, and opportunity", "enablementOwner"),
            Link(CopilotAdoptionScoring.AdoptionActionCodes.Advocate, "frontierFirmResources", "Frontier Firm resources", "https://www.microsoft.com/en-us/worklab/frontier-firm-resources", "Journey to the Frontier Firm", "enablementOwner"),

            Link(CopilotAdoptionScoring.AdoptionActionCodes.Review, "licenseAllocationGuide", "License allocation guidance", "https://aka.ms/Copilot/LicenseAllocationGuide", "Microsoft 365 Copilot license allocation guidance for rapid value", "admin"),
            Link(CopilotAdoptionScoring.AdoptionActionCodes.Excluded, "licenseAllocationGuide", "License allocation guidance", "https://aka.ms/Copilot/LicenseAllocationGuide", "Microsoft 365 Copilot license allocation guidance for rapid value", "admin"),

            Link(UnlicensedActionCode, "copilotReadinessReport", "Microsoft Copilot Readiness Report", "https://learn.microsoft.com/en-us/microsoft-365/admin/activity-reports/microsoft-365-copilot-readiness", "Microsoft Copilot Readiness Report - Microsoft 365 admin | Microsoft Learn", "budgetOwner"),
            Link(UnlicensedActionCode, "implementationSummaryGuide", "Implementation Summary Guide for Leaders", "https://aka.ms/Copilot/ImplementationSummaryGuide", "Microsoft 365 Copilot Implementation executive summary", "budgetOwner"),
        };

        public static IReadOnlyList<AdoptionGuidanceLink> All => Links.Select(Clone).ToList();

        public static IReadOnlyList<AdoptionGuidanceLink> ForAction(string actionCode)
        {
            if (string.IsNullOrWhiteSpace(actionCode)) return new List<AdoptionGuidanceLink>();

            return Links
                .Where(l => string.Equals(l.ActionCode, actionCode, StringComparison.OrdinalIgnoreCase))
                .Select(Clone)
                .ToList();
        }

        public static string TitlesForAction(string actionCode)
        {
            return string.Join(" | ", ForAction(actionCode).Select(l => l.Title));
        }

        public static string UrlsForAction(string actionCode)
        {
            return string.Join(" | ", ForAction(actionCode).Select(l => l.Url));
        }

        private static AdoptionGuidanceLink Link(
            string actionCode,
            string titleKey,
            string title,
            string url,
            string expectedTitle,
            string audience)
        {
            return new AdoptionGuidanceLink
            {
                ActionCode = actionCode,
                TitleKey = titleKey,
                Title = title,
                Url = url,
                ExpectedTitle = expectedTitle,
                Audience = audience,
                Publisher = "Microsoft",
                CatalogueVersion = Version,
            };
        }

        private static AdoptionGuidanceLink Clone(AdoptionGuidanceLink link)
        {
            return new AdoptionGuidanceLink
            {
                ActionCode = link.ActionCode,
                TitleKey = link.TitleKey,
                Title = link.Title,
                Url = link.Url,
                ExpectedTitle = link.ExpectedTitle,
                Audience = link.Audience,
                Publisher = link.Publisher,
                CatalogueVersion = link.CatalogueVersion,
            };
        }
    }
}
