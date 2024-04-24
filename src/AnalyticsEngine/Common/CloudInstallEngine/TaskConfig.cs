using CloudInstallEngine.Models;
using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;

namespace CloudInstallEngine
{
    public class TaskConfig : Dictionary<string, string>
    {
        const string CONFIG_KEY_NAME = "name";
        public TaskConfig()
        {
        }
        public TaskConfig(TaskConfig config)
        {
            Add(config);
        }

        public string ResourceName
        {
            get
            {
                if (this.ContainsKey(CONFIG_KEY_NAME))
                {
                    return this[CONFIG_KEY_NAME];
                }
                return null;
            }
        }

        public static TaskConfig GetConfigForName(string resourceName)
        {
            var c = new TaskConfig
            {
                { CONFIG_KEY_NAME, resourceName }
            };
            return c;
        }
        public static TaskConfig GetConfigForPropAndVal(string key, string val)
        {
            var c = new TaskConfig();
            c.AddSetting(key, val);
            return c;
        }

        public static TaskConfig NoConfig => new TaskConfig();

        public TaskConfig AddSetting(string key, string val)
        {
            if (val == null)
            {
                throw new ArgumentNullException(nameof(val), $"Cannot save null values - {nameof(key)}:{key}");
            }
            if (ContainsKey(key))
            {
                throw new ArgumentException($"Already have configuration item '{key}'");
            }
            this.Add(key, val);

            return this;
        }

        internal string GetConfigValue(string key)
        {
            if (base.ContainsKey(key))
            {
                return base[key];
            }
            else
            {
                throw new InstallException($"No configuration by name '{key}'");
            }
        }
        internal string GetNameConfigValue()
        {
            return GetConfigValue(CONFIG_KEY_NAME);
        }

        /// <summary>
        /// To Json compatible object [{ name: "", value: "" }]
        /// </summary>
        public object ToArmParamsObject()
        {
            return ToArmParamsObject(null);
        }
        public object ToArmParamsObject(dynamic anonMergeObj)
        {
            if (anonMergeObj == null)
            {
                return ToArmParamsObject(null);
            }
            else
            {
                return ToArmParamsObject(ConvertAnonymousToDictionary(anonMergeObj));
            }
        }

        static Dictionary<string, object> ConvertAnonymousToDictionary(object content)
        {
            var props = content.GetType().GetProperties();
            var pairDictionary = props.ToDictionary(x => x.Name, x => x.GetValue(content, null));
            return pairDictionary;
        }

        public object ToArmParamsObject(IDictionary<string, object> mergeObj)
        {
            var eo = new ExpandoObject();
            var eoColl = (ICollection<KeyValuePair<string, object>>)eo;

            foreach (var kvp in this)
            {
                eoColl.Add(new KeyValuePair<string, object>(kvp.Key, new { value = kvp.Value }));
            }
            if (mergeObj != null)
            {
                foreach (var kvp in mergeObj)
                {
                    eoColl.Add(new KeyValuePair<string, object>(kvp.Key, kvp.Value));
                }
            }
            dynamic eoDynamic = eo;

            return eoDynamic;
        }


        /// <summary>
        /// Return a config object where config names match a list
        /// </summary>
        public TaskConfig FilterParams(IEnumerable<string> armParamNames)
        {
            var c = new TaskConfig();

            foreach (var armParamName in armParamNames)
                if (!this.ContainsKey(armParamName))
                {
                    throw new ArgumentOutOfRangeException(nameof(armParamNames), $"No configuration item found with name '{armParamName}'");
                }

            var matchingConfig = this.Where(k => armParamNames.Contains(k.Key));
            foreach (var kvp in matchingConfig)
                c.Add(kvp.Key, kvp.Value);
            return c;
        }

        public TaskConfig Add(TaskConfig config)
        {
            foreach (var kvp in config)
            {
                this.Add(kvp.Key, kvp.Value);
            }
            return this;
        }

        public TaskConfig Clone()
        {
            return new TaskConfig(this);
        }
    }
}
