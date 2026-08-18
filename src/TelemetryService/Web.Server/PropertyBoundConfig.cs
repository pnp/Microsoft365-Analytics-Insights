using System.Reflection;

namespace Web.Config
{

    public abstract class PropertyBoundConfig
    {
        /// <summary>
        /// Load automatically config properties
        /// </summary>
        /// <param name="config">Config to read</param>
        /// <exception cref="ArgumentNullException">If config to read is null</exception>
        /// <exception cref="ConfigurationMissingException">If config has missing required properties</exception>
        public PropertyBoundConfig(Microsoft.Extensions.Configuration.IConfiguration config)
        {
            if (config is null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            // Set config props
            var allProps = this.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
            foreach (var prop in allProps)
            {
                // Set values
                var configValAtt = prop.GetCustomAttribute<ConfigValueAttribute>();
                if (configValAtt != null)
                {
                    var configVal = config[prop.Name];
                    if (!configValAtt.Optional && string.IsNullOrEmpty(configVal))
                    {
                        throw new ConfigurationMissingException(prop.Name);
                    }
                    prop.SetValue(this, configVal);
                }

                // Set config sub-sections
                var configSectionAtt = prop.GetCustomAttribute<ConfigSectionAttribute>();
                if (configSectionAtt != null)
                {
                    var configSection = config.GetSection(configSectionAtt.SectionName);
                    var instance = Activator.CreateInstance(prop.PropertyType, configSection);

                    prop.SetValue(this, instance);
                }
            }
        }
    }

    public class ConfigurationMissingException : Exception
    {
        public ConfigurationMissingException(string propertyName) : base($"Missing required configuration value '{propertyName}'")
        {
        }
    }

    /// <summary>
    /// Property comes from supplied config section
    /// </summary>
    public class ConfigValueAttribute : Attribute
    {
        public ConfigValueAttribute() { }
        public ConfigValueAttribute(bool optional)
        {
            this.Optional = optional;
        }
        public bool Optional { get; set; } = false;
    }

    /// <summary>
    /// Property has a sub-section
    /// </summary>
    public class ConfigSectionAttribute : Attribute
    {
        public ConfigSectionAttribute(string sectionName)
        {
            SectionName = sectionName;
        }
        public string SectionName { get; set; } = string.Empty;
    }
}
