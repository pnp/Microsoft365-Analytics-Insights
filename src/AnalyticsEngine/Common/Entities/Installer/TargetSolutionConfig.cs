using System.Collections.Generic;

namespace Common.Entities.Installer
{
    /// <summary>
    /// What do we want to setup?
    /// </summary>
    public class TargetSolutionConfig : BaseConfig
    {
        public TargetSolutionConfig()
        {
            ImportTaskSettings = new ImportTaskSettings();
        }

        public ImportTaskSettings ImportTaskSettings { get; set; }
    }
}
