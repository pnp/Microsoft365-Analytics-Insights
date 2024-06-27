using Azure;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.Resources.Models;
using CloudInstallEngine.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace CloudInstallEngine
{
    /// <summary>
    /// Install task that doesn't have a container (resource-group)
    /// </summary>
    public abstract class BaseInstallTask
    {
        /// <summary>
        /// Return object because the installer can't dynamically return T
        /// </summary>
        public abstract Task<object> ExecuteTask(object contextArg);
        public virtual async Task<object> ExecuteTask() { return await ExecuteTask(null); }
        public virtual string TaskName => this.GetType().Name;

        protected readonly TaskConfig _config;
        protected readonly ILogger _logger;

        protected BaseInstallTask(TaskConfig config, ILogger logger)
        {
            _config = config;
            _logger = logger;
        }

        protected T EnsureContextArgType<T>(object contextArg)
        {
            if (contextArg == null) throw new InstallException($"Argument passed to task {this.TaskName} from previous step is null");
            if (contextArg.GetType() != typeof(T))
            {
                throw new InstallException($"{this.GetType().Name} was expecting a result from previous task of type '{typeof(T).Name}'. Instead, it got a result of type '{contextArg.GetType().Name}'");
            }
            return (T)contextArg;
        }

        protected async Task EnsureTagsOnExisting(IDictionary<string, string> existingTags, IDictionary<string, string> wanted, TagResource tagResource)
        {
            var tagPatch = new TagResourcePatch() { PatchMode = TagPatchMode.Merge };
            bool addedTags = false;
            foreach (var tag in wanted)
            {
                if (!existingTags.ContainsKey(tag.Key))
                {
                    tagPatch.TagValues.Add(tag.Key, tag.Value);
                    addedTags = true;
                }
            }
            if (addedTags)
            {
                var r = await tagResource.GetAsync();
                await r.Value.UpdateAsync(WaitUntil.Completed, tagPatch);
                _logger.LogInformation($"Updated resource tags");
            }
        }

        protected void EnsureTagsOnNew(IDictionary<string, string> existingTags, IDictionary<string, string> wanted)
        {
            foreach (var tag in wanted)
            {
                if (!existingTags.ContainsKey(tag.Key))
                {
                    existingTags.Add(tag.Key, tag.Value);
                }
            }
        }
    }

    /// <summary>
    /// Install task that returns something. Overrides base implementation and gives a typed version
    /// </summary>
    public abstract class ResourceInstallTask<TASKRESULTINGRESOURCE> : BaseInstallTask, IReturnResultTask<TASKRESULTINGRESOURCE>
    {
        protected ResourceInstallTask(TaskConfig config, ILogger logger) : base(config, logger)
        {
        }

        public sealed override async Task<object> ExecuteTask(object contextArg)
        {
            return await ExecuteTaskReturnResult(contextArg);
        }

        public abstract Task<TASKRESULTINGRESOURCE> ExecuteTaskReturnResult(object contextArg);
    }

    /// <summary>
    /// Task that returns something created/got in a container (resource group)
    /// </summary>
    public abstract class ResourceInstallTaskInContainer<TASKRESULTINGRESOURCE, RESOURCESCONTAINERTYPE>
        : ResourceInstallTask<TASKRESULTINGRESOURCE>, IContainerHostedResource<RESOURCESCONTAINERTYPE>
    {
        protected ResourceInstallTaskInContainer(TaskConfig config, ILogger logger) : base(config, logger)
        {
        }

        /// <summary>
        /// Cloud resources container. Set when task is executed by a InstallJobInContainerJob
        /// </summary>
        public RESOURCESCONTAINERTYPE Container { get; set; }
    }
}
