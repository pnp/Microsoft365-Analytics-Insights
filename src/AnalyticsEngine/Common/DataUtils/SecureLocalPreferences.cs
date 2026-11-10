using Newtonsoft.Json;
using System;
using System.IO;
using System.Security.Cryptography;

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

        /// <summary>
        /// Loads the preferences saved for <typeparamref name="T"/>, or the defaults when none are saved.
        /// </summary>
        /// <param name="reportUnreadable">
        /// Told why, when a saved file can't be read and the defaults are used instead. A file Windows can't decrypt for
        /// this account (written by another account or machine, or after its data-protection keys changed), or a damaged
        /// one, must not stop the application starting: saving preferences again replaces it.
        /// </param>
        public static T Load<T>(Action<string> reportUnreadable = null) where T : SecureLocalPreferences, new()
        {
            T defaultData = (T)Activator.CreateInstance(typeof(T));

            if (File.Exists(defaultData.FullFileName))
            {
                string problem;
                try
                {
                    var binaryData = File.ReadAllBytes(defaultData.FullFileName);
                    var json = StringUtils.UnprotectString(binaryData);
                    Console.WriteLine($"Read file '{defaultData.FullFileName}' for type '{typeof(T).Name}'");
                    var loaded = JsonConvert.DeserializeObject<T>(json);
                    if (loaded != null) return loaded;
                    problem = "it holds no preferences";
                }
                catch (Exception ex) when (ex is CryptographicException || ex is JsonException || ex is IOException || ex is UnauthorizedAccessException)
                {
                    problem = $"{ex.GetType().Name}: {ex.Message.Trim()}";
                }

                var message = $"The saved preferences in '{defaultData.FullFileName}' could not be read ({problem}), so the defaults " +
                    "are in use. Saving the preferences again replaces the file.";
                Console.WriteLine(message);
                reportUnreadable?.Invoke(message);
                return defaultData;
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
