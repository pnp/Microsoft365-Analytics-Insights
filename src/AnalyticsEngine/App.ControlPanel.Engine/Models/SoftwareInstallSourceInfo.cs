using Azure.Storage.Blobs.Models;
using System;
using System.Collections.Generic;
using System.IO;

namespace App.ControlPanel.Engine.Entities
{
    /// <summary>
    /// Solution install sources can be either local or Azure blob hosted. Abstract implementation
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

    public class AzureStorageInstallSourceInfo : SolutionSourceInfo<BlobItemInfo>
    {
        /// <summary>
        /// Valid set of files for a deploy?
        /// </summary>
        public override bool IsValid
        {
            get
            {
                if (this.Components.Count == this.MAX_ZIPS)
                {
                    foreach (BlobItemInfo blobInfo in Components.Values)
                    {
                        if (blobInfo == null || blobInfo.Blob == null || blobInfo.LastModified == DateTime.MinValue)
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
    /// Metadata about a file in AZ storage. Can't use BlobItem as has no public constructor.
    /// </summary>
    public class BlobItemInfo
    {
        public BlobItemInfo()
        {
            this.LastModified = DateTime.MinValue;
            this.Blob = null;
        }

        public BlobItem Blob { get; set; }
        public DateTime LastModified { get; set; }
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
