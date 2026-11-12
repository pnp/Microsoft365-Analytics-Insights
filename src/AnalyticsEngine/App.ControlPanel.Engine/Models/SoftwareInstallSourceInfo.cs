using System.Collections.Generic;
using System.IO;

namespace App.ControlPanel.Engine.Entities
{
    /// <summary>
    /// Where the solution's install packages are. Abstract implementation; <see cref="LocalStorageInstallSourceInfo"/>
    /// is the one the installer uses, for a downloaded release and for a local release override alike.
    /// </summary>
    /// <typeparam name="T">Type of object that points to one of the solution components</typeparam>
    public abstract class SolutionSourceInfo<T> where T : new()
    {
        public Dictionary<SoftwareComponent, T> Components { get; set; } = new Dictionary<SoftwareComponent, T>();

        [Newtonsoft.Json.JsonIgnore]
        public abstract bool IsValid { get; }

        [Newtonsoft.Json.JsonIgnore]
        public int MAX_ZIPS => 5;

        /// <summary>
        /// Get existing or create new component location. New components are empty but will be filled in later.
        /// </summary>
        public T GetSolutionComponentLocation(SoftwareComponent component)
        {
            if (!Components.ContainsKey(component))
            {
                Components.Add(component, new T());
            }
            return Components[component];
        }
    }

    public class LocalStorageInstallSourceInfo : SolutionSourceInfo<LocalStorageBlobInfo>
    {
        /// <summary>
        /// Valid set of files for a deploy?
        /// </summary>
        [Newtonsoft.Json.JsonIgnore]
        public override bool IsValid
        {
            get
            {
                if (this.Components.Count == this.MAX_ZIPS)
                {
                    foreach (var blobInfo in Components.Values)
                    {
                        if (blobInfo == null || !File.Exists(blobInfo.FileLocation) || !blobInfo.FileLocation.EndsWith(".zip"))
                        {
                            return false;
                        }
                    }

                    return true;
                }

                return false;
            }
        }
    }

    /// <summary>
    /// Where a file is located on the local file system
    /// </summary>
    public class LocalStorageBlobInfo
    {
        public string FileLocation { get; set; }
    }

    public enum SoftwareComponent
    {
        Unknown = 0,
        AITracker,
        WebJobActivity,
        WebJobAppInsights,
        ControlPanel,
        WebSite
    }
}
