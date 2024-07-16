using Newtonsoft.Json;
using System;
using System.IO;

namespace DataUtils
{
    /// <summary>
    /// A class to load/save preferences securely on the local machine
    /// </summary>
    public abstract class SecureLocalPreferences
    {
        protected abstract string FileTitle { get; }
        public FileInfo SaveToTempFile()
        {
            Directory.CreateDirectory(StringUtils.TempDirPath);
            File.WriteAllBytes(FullFileName, StringUtils.ProtectString(JsonConvert.SerializeObject(this)));
            Console.WriteLine($"Wrote '{FullFileName}'");
            return new FileInfo(FullFileName);
        }

        public void DeleteTempFile()
        {
            try
            {
                File.Delete(FullFileName);
            }
            catch (IOException ex)
            {
                Console.WriteLine($"Got error '{ex.Message}' deleting '{FullFileName}'");
                return;
            }
            Console.WriteLine($"Deleted '{FullFileName}'");
        }

        public static T Load<T>() where T : SecureLocalPreferences, new()
        {
            T defaultData = (T)Activator.CreateInstance(typeof(T));

            if (File.Exists(defaultData.FullFileName))
            {
                var binaryData = File.ReadAllBytes(defaultData.FullFileName);
                var json = StringUtils.UnprotectString(binaryData);
                Console.WriteLine($"Read file '{defaultData.FullFileName}' for type '{typeof(T).Name}'");
                return JsonConvert.DeserializeObject<T>(json);
            }
            else
            {
                Console.WriteLine($"No file exists at '{defaultData.FullFileName}' for type '{typeof(T).Name}' - returning default object");
                return defaultData;
            }
        }

        string FullFileName => $"{StringUtils.TempDirPath}\\{FileTitle}";
    }
}
