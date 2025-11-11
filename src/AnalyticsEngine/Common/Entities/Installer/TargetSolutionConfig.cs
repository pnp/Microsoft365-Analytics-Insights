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
                return _importTaskSettings;
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

    }

    /// <summary>
    /// Insights can be tailored for specific imports. Adoptify is basically hard-coded imports - NOW DEPRECATED
    /// </summary>
    public enum SolutionImportType
    {
        CustomOrInsights
    }
}
