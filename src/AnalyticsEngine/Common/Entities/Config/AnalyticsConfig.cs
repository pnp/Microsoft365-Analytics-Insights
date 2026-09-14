using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.UserSecrets;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace Common.Entities.Config
{
    /// <summary>
    /// The solution's configuration source, replacing <c>System.Configuration.ConfigurationManager</c>
    /// and the App.config/Web.config XML it read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sources, in increasing order of precedence:
    /// </para>
    /// <list type="number">
    /// <item><c>appsettings.json</c> - checked in, holds only non-secret defaults.</item>
    /// <item><c>appsettings.{Environment}.json</c> - optional, for local overrides.</item>
    /// <item><b>User secrets</b>, in Development only - where a developer's own credentials belong.
    /// They live outside the repository, so there is no longer a gitignored file in the working tree
    /// that a careless commit could publish.</item>
    /// <item><b>Environment variables</b> - how Azure App Service supplies configuration, and the
    /// highest precedence so a deployment always wins.</item>
    /// </list>
    /// <para>
    /// Azure App Service is handled without any special casing on our side.
    /// <c>AddEnvironmentVariables</c> already understands the prefixes App Service injects:
    /// <c>SQLAZURECONNSTR_</c>, <c>SQLCONNSTR_</c> and <c>CUSTOMCONNSTR_</c> all surface under
    /// <c>ConnectionStrings:</c>, and app settings arrive as plain variables. So a connection string
    /// saved by the installer as type SQLAzure is found here as
    /// <c>ConnectionStrings:SPOInsightsEntities</c> exactly as it is locally, with no code that knows
    /// the difference.
    /// </para>
    /// <para>
    /// The shape deliberately mirrors the old <c>ConfigurationManager</c> API - <c>AppSettings.Get()</c>
    /// and <c>ConnectionStrings[name].ConnectionString</c> - so the ~145 call sites across the
    /// solution did not each have to be rewritten and reviewed. The indirection is worth removing
    /// later; doing it in the same change as the source swap would have made both unreviewable.
    /// </para>
    /// </remarks>
    public static class AnalyticsConfig
    {
        private static readonly Lazy<IConfigurationRoot> _root = new Lazy<IConfigurationRoot>(Build);
        private static IConfigurationRoot _override;

        /// <summary>The live configuration. Built once per process.</summary>
        public static IConfigurationRoot Root => _override ?? _root.Value;

        public static AppSettingsAccessor AppSettings { get; } = new AppSettingsAccessor();

        public static ConnectionStringsAccessor ConnectionStrings { get; } = new ConnectionStringsAccessor();

        private static IConfigurationRoot Build()
        {
            // ASPNETCORE_ENVIRONMENT is what the web app sets; DOTNET_ENVIRONMENT is the generic host's
            // equivalent and is what the web jobs use.
            var environment =
                Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
                ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
                ?? "Production";

            var builder = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
                .AddJsonFile($"appsettings.{environment}.json", optional: true, reloadOnChange: false);

            if (string.Equals(environment, "Development", StringComparison.OrdinalIgnoreCase))
            {
                AddDeveloperSecrets(builder);
            }

            return builder.AddEnvironmentVariables().Build();
        }

        /// <summary>
        /// Adds the developer's user-secrets store(s).
        /// </summary>
        /// <remarks>
        /// <para>
        /// The solution keeps ONE shared store, declared as <c>pnp-m365-analytics-insights</c> on
        /// <c>Common.Entities</c> and repeated as the <c>UserSecretsId</c> of every executable and test
        /// project. One store is what a developer wants: the web app, both web jobs, the installer and
        /// the tests all authenticate against the same tenant with the same app registration.
        /// </para>
        /// <para>
        /// It is read from <b>this</b> assembly rather than the entry assembly, because the entry
        /// assembly is the wrong thing to ask - under <c>dotnet test</c> it is <c>testhost</c>, which
        /// carries no id at all, so an entry-assembly lookup silently finds nothing for the whole suite.
        /// </para>
        /// <para>
        /// The entry assembly's own store is then added as well, when it declares a DIFFERENT id. That
        /// is a deliberate safety net for the sharpest edge here: Visual Studio's "Manage User Secrets"
        /// generates a fresh random id into whichever project it is invoked on, and a developer who does
        /// that on the web app gets a store nothing reads. Worse, it looks like it works -
        /// <c>WebApplication.CreateBuilder</c> loads that store into <c>builder.Configuration</c> in
        /// Development, but nothing in this solution reads configuration from the host builder; every
        /// read goes through this class. So the secrets are loaded, ignored, and give no error. Reading
        /// the entry assembly's store too means such a store still works, and it takes precedence
        /// because it is the more specific of the two.
        /// </para>
        /// </remarks>
        private static void AddDeveloperSecrets(IConfigurationBuilder builder)
        {
            var shared = typeof(AnalyticsConfig).Assembly;
            builder.AddUserSecrets(shared, optional: true);

            var entry = System.Reflection.Assembly.GetEntryAssembly();
            if (entry != null && entry != shared && SecretsIdOf(entry) is string entryId
                && !string.Equals(entryId, SecretsIdOf(shared), StringComparison.Ordinal))
            {
                builder.AddUserSecrets(entry, optional: true);
            }
        }

        private static string SecretsIdOf(System.Reflection.Assembly assembly) =>
            assembly.GetCustomAttribute<UserSecretsIdAttribute>()?.UserSecretsId;


        /// <summary>Replaces the configuration. Test seam only.</summary>
        public static void UseForTesting(IEnumerable<KeyValuePair<string, string>> values)
        {
            _override = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        }

        /// <summary>
        /// Builds a fresh configuration from the real sources, without disturbing the process one.
        /// Test seam only.
        /// </summary>
        /// <remarks>
        /// Exists so the App Service story above can be proved rather than asserted in a comment. The
        /// live <see cref="Root"/> is built once and cached, so a test cannot set an environment
        /// variable and observe it; this rebuilds through the identical code path.
        /// </remarks>
        public static IConfigurationRoot BuildForTesting() => Build();

        /// <summary>Restores the process configuration after <see cref="UseForTesting"/>.</summary>
        public static void ResetForTesting() => _override = null;

        public sealed class AppSettingsAccessor
        {
            /// <summary>The setting, or null when it is absent or empty.</summary>
            public string Get(string key)
            {
                var value = Root[key];
                return string.IsNullOrEmpty(value) ? null : value;
            }

            public string this[string key]
            {
                get => Get(key);
                set => Set(key, value);
            }

            /// <summary>
            /// Overrides a setting for the current process.
            /// </summary>
            /// <remarks>
            /// Tests use this to exercise configuration-dependent behaviour, which the old
            /// <c>ConfigurationManager.AppSettings.Set</c> allowed. Writing through
            /// <see cref="IConfigurationRoot"/> sets it on the in-memory layer only - nothing is written
            /// to appsettings.json, and the value is gone on the next process.
            /// </remarks>
            public void Set(string key, string value) => Root[key] = value;
        }

        public sealed class ConnectionStringsAccessor
        {
            /// <summary>
            /// The named connection string, or null when it is absent - matching the old
            /// <c>ConfigurationManager.ConnectionStrings[name]</c>, which returned null rather than throwing.
            /// </summary>
            public ConnectionStringEntry this[string name]
            {
                get
                {
                    var value = Root.GetConnectionString(name);
                    return string.IsNullOrEmpty(value) ? null : new ConnectionStringEntry(name, value);
                }
            }

            /// <summary>
            /// Adds or replaces a connection string for the current process. Test seam, mirroring what
            /// <c>ConfigurationManager.ConnectionStrings.Add</c> allowed.
            /// </summary>
            public void Add(ConnectionStringEntry entry)
            {
                if (entry == null) throw new ArgumentNullException(nameof(entry));
                Root[$"ConnectionStrings:{entry.Name}"] = entry.ConnectionString;
            }

            public void Add(string name, string connectionString) =>
                Root[$"ConnectionStrings:{name}"] = connectionString;
        }

        /// <summary>
        /// Stands in for <c>ConnectionStringSettings</c> so call sites keep their shape.
        /// </summary>
        /// <remarks>
        /// Immutable on purpose. It is a <i>snapshot</i> read out of the configuration, not a live
        /// handle to it, so assigning to a property would change nothing that anyone else can see.
        /// Under <c>ConfigurationManager</c> the equivalent object was a live element, and code did
        /// write to it - <c>ActivityApiDbStressRunner.ForceRuntimeConnectionString</c> did exactly
        /// that, and silently stopped working when the type changed underneath it. Making the
        /// properties get-only turns that class of mistake into a compile error; use
        /// <see cref="ConnectionStringsAccessor.Add(string, string)"/> to actually change a value.
        /// </remarks>
        public sealed class ConnectionStringEntry
        {
            public ConnectionStringEntry(string name, string connectionString)
            {
                Name = name;
                ConnectionString = connectionString;
            }

            /// <summary>
            /// Overload kept so call sites that passed a provider name still compile. The provider name
            /// is ignored - see <see cref="ProviderName"/>.
            /// </summary>
            public ConnectionStringEntry(string name, string connectionString, string providerName)
                : this(name, connectionString)
            {
                ProviderName = providerName;
            }

            public string Name { get; }

            public string ConnectionString { get; }

            /// <summary>
            /// Retained so call sites that read it still compile. It is ignored: EF's provider is chosen
            /// in code by <c>SPOInsightsDBConfiguration</c>, not by a per-connection-string name.
            /// </summary>
            public string ProviderName { get; }
        }
    }
}
