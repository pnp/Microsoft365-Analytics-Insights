using CloudInstallEngine.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace CloudInstallEngine
{
    /// <summary>
    /// Base install job. Runs a list of tasks in sequence.
    /// </summary>
    public abstract class SequentialTaskListInstallJob
    {
        public SequentialTaskListInstallJob(ILogger logger)
        {
            Logger = logger;
        }

        public ILogger Logger { get; set; }
        public bool HasRun { get; set; } = false;
        public Dictionary<BaseInstallTask, object> TaskResults { get; set; } = new Dictionary<BaseInstallTask, object>();

        protected List<List<BaseInstallTask>> _installTaskListWithChildren = new List<List<BaseInstallTask>>();

        public virtual async Task Install()
        {
            await Install(null);
        }
        public virtual async Task Install(object previousRunResult)
        {
            // Loop through all tasks, running each one + all children
            foreach (var taskWithDependencyTasks in _installTaskListWithChildren)
            {
                foreach (var thisTask in taskWithDependencyTasks)
                {
                    try
                    {
                        previousRunResult = await ProcessTask(thisTask, previousRunResult);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError($"Unexpected error on stage {thisTask.TaskName}:");
                        Logger.LogError(ex, ex.Message);
                        throw;      // Error will be logged by parent
                    }

                    // Remember result
                    TaskResults.Add(thisTask, previousRunResult);
                }
            }
        }

        #region Task Registration

        public void AddTask(BaseInstallTask task)
        {
            _installTaskListWithChildren.Add(new List<BaseInstallTask>() { task });
        }
        public void AddTask(params BaseInstallTask[] tasks)
        {
            _installTaskListWithChildren.Add(new List<BaseInstallTask>(tasks));
        }
        public void AddTasks(List<BaseInstallTask> tasks)
        {
            _installTaskListWithChildren.Add(tasks);
        }

        #endregion

        protected virtual async Task<object> ProcessTask(BaseInstallTask thisTask, object previousRunResult)
        {
            // Execute task
            return await thisTask.ExecuteTask(previousRunResult);
        }
    }

    /// <summary>
    /// Base task installer for jobs that install in a container.
    /// Container is loaded by specific loader on Install method and assigned to each task by this job object.
    /// </summary>
    public abstract class InstallJobInContainerJob<RESOURCESCONTAINERTYPE> : SequentialTaskListInstallJob
    {
        private readonly IReturnResultTask<RESOURCESCONTAINERTYPE> _containerLoader;

        /// <summary>
        /// Create new job class with a container loader.
        /// </summary>
        public InstallJobInContainerJob(ILogger logger, IReturnResultTask<RESOURCESCONTAINERTYPE> containerLoader) : base(logger)
        {
            _containerLoader = containerLoader;
        }

        /// <summary>
        /// Container for job resources/tasks. Set on Install & assigned to each task as they are processed. 
        /// </summary>
        public RESOURCESCONTAINERTYPE ResultingContainer { get; set; }

        // Override default install so we can set the container from the loader 1st.
        public async override Task Install()
        {
            await Install(null);
        }

        public async override Task Install(object previousRunResult)
        {
            // Ensure container exists 1st
            var resourcesContainer = await _containerLoader.ExecuteTaskReturnResult(null);
            if (resourcesContainer == null)
            {
                throw new ArgumentNullException(nameof(resourcesContainer));
            }
            this.ResultingContainer = resourcesContainer;

            HasRun = true;

            await base.Install(previousRunResult);
        }

        protected T GetTaskResult<T>(BaseInstallTask task)
        {
            if (!HasRun)
            {
                throw new InstallException("Installer hasn't run - can't return results");
            }

            if (TaskResults.ContainsKey(task))
            {
                return (T)TaskResults[task];
            }
            else
            {
                throw new InstallException($"No results for task '{task.TaskName}'");
            }
        }

        protected override Task<object> ProcessTask(BaseInstallTask thisTask, object previousRunResult)
        {
            // Set task container
            if (thisTask is IContainerHostedResource<RESOURCESCONTAINERTYPE>)
            {
                var containerTask = (IContainerHostedResource<RESOURCESCONTAINERTYPE>)thisTask;
                containerTask.Container = ResultingContainer;
            }

            return base.ProcessTask(thisTask, previousRunResult);
        }
    }
}
