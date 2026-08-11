using App.ControlPanel.Engine;
using Common.Entities.Installer;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Tests.UnitTests
{
    /// <summary>
    /// Issue #228: a private (VNet, public access disabled) deployment leaves a non-Premium Service Bus namespace
    /// unreachable, which silently kills the Teams calls import. The installer must warn before it happens.
    /// </summary>
    [TestClass]
    public class PreInstallAdvisorTests
    {
        private static BaseSolutionInstallConfig PrivateConfigWithServiceBus()
        {
            return new BaseSolutionInstallConfig
            {
                ServiceBusEnabled = true,
                ServiceBusName = "sb-contoso-analytics",
                NetworkConfig = new VNetConfig { Enabled = true, AllowPublicAccess = false }
            };
        }

        [TestMethod]
        public void Warns_WhenPrivateDeploymentUsesServiceBus()
        {
            var warning = PreInstallAdvisor.GetServiceBusPrivateDeploymentWarning(PrivateConfigWithServiceBus());

            Assert.IsNotNull(warning, "A private deployment with the Teams calls import on must warn about the Premium requirement.");
            StringAssert.Contains(warning, "PREMIUM");
            StringAssert.Contains(warning, "sb-contoso-analytics");
        }

        [TestMethod]
        public void NoWarning_WhenPublicAccessStaysEnabled()
        {
            var config = PrivateConfigWithServiceBus();
            config.NetworkConfig.AllowPublicAccess = true;

            Assert.IsNull(PreInstallAdvisor.GetServiceBusPrivateDeploymentWarning(config),
                "With public access still allowed the namespace stays reachable whatever its SKU.");
        }

        [TestMethod]
        public void NoWarning_WhenVNetDisabled()
        {
            var config = PrivateConfigWithServiceBus();
            config.NetworkConfig.Enabled = false;

            Assert.IsNull(PreInstallAdvisor.GetServiceBusPrivateDeploymentWarning(config));
        }

        [TestMethod]
        public void NoWarning_WhenServiceBusDisabled()
        {
            var config = PrivateConfigWithServiceBus();
            config.ServiceBusEnabled = false;

            Assert.IsNull(PreInstallAdvisor.GetServiceBusPrivateDeploymentWarning(config),
                "Service Bus is only used by the Teams calls import - nothing to warn about when it's off.");
        }

        [TestMethod]
        public void NoWarning_ForNullConfig()
        {
            Assert.IsNull(PreInstallAdvisor.GetServiceBusPrivateDeploymentWarning(null));
        }

        /// <summary>
        /// The install summary must turn the warning into an actionable next step, so it survives to the end of
        /// a long install log.
        /// </summary>
        [TestMethod]
        public void InstallSummary_SurfacesServiceBusNextStep()
        {
            var summary = new InstallSummary();
            summary.AddError("ServiceBus", "Service Bus namespace 'sb-contoso-analytics' is on the Standard SKU but private endpoints require Premium.");

            var logger = new CollectingLogger();
            summary.Print(logger);

            var text = string.Join("\n", logger.Messages);
            StringAssert.Contains(text, "Next steps:");
            StringAssert.Contains(text, "migrate the namespace to Premium");
        }

        private class CollectingLogger : Microsoft.Extensions.Logging.ILogger
        {
            public readonly System.Collections.Generic.List<string> Messages = new System.Collections.Generic.List<string>();
            public System.IDisposable BeginScope<TState>(TState state) => NullScope.Instance;
            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, System.Exception exception, System.Func<TState, System.Exception, string> formatter)
            {
                Messages.Add(formatter(state, exception));
            }

            private class NullScope : System.IDisposable
            {
                public static readonly NullScope Instance = new NullScope();
                public void Dispose() { }
            }
        }
    }
}
