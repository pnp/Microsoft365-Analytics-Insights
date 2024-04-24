using App.ControlPanel.Engine;

namespace App.ControlPanel
{
    /// <summary>
    /// A screen compnent that can load and read configuration settings
    /// </summary>
    public interface ISolutionConfigurableComponent
    {
        void ConfigureUI(SolutionInstallConfig config);

        SolutionInstallConfig GetConfigurationState();
    }
}
