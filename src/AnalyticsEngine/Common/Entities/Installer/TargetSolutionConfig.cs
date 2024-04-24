using System.Collections.Generic;

namespace Common.Entities.Installer
{
    /// <summary>
    /// What do we want to setup?
    /// </summary>
    public class TargetSolutionConfig : BaseConfig
    {
        public const string LANG_ENGLISH = "en";
        public const string LANG_ESPAÑOL = "es";        // ¡olé!

        private ImportTaskSettings _importTaskSettings = null;
        public TargetSolutionConfig()
        {
            ImportTaskSettings = new ImportTaskSettings();
            SolutionLanguageCode = LANG_ENGLISH;
        }

        public ImportTaskSettings ImportTaskSettings
        {
            get
            {
                if (SolutionTargeted == SolutionImportType.Adoptify)
                {
                    // Adoptify has hard-coded import settings. This might need reviewing.
                    return new ImportTaskSettings()
                    {
                        ActivityLog = false,
                        Calls = true,                   // Call details; modalities etc are needed
                        GraphTeams = true,              // Channel stats are gotten through usage reports, but we need reactions which we can only get from channels
                        GraphUsageReports = true,       // Obviously needed per user
                        GraphUserApps = true,           // App quests
                        GraphUsersMetadata = true,      // For now import this as it might be useful
                        WebTraffic = false              // No web code is deployed for Adoptify
                    };
                }
                else return _importTaskSettings;
            }
            set
            {
                _importTaskSettings = value;
            }
        }


        public AdoptifySolutionInstallConfig Adoptify { get; set; } = new AdoptifySolutionInstallConfig();

        public SolutionImportType SolutionTargeted { get; set; }

        /// <summary>
        /// EN, ES, etc
        /// </summary>
        public string SolutionLanguageCode { get; set; }

        public override List<string> ValidatInputAndGetErrors()
        {
            var errors = new List<string>();
            if (SolutionTargeted == SolutionImportType.Adoptify && (SolutionLanguageCode != LANG_ENGLISH || SolutionLanguageCode != LANG_ESPAÑOL))
            {
                errors.Add("Select a valid target language");
            }
            return errors;
        }
    }

    /// <summary>
    /// Insights can be tailored for specific imports. Adoptify is basically hard-coded imports
    /// </summary>
    public enum SolutionImportType
    {
        CustomOrInsights,
        Adoptify
    }
}
