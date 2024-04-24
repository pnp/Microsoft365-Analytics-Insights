using Microsoft.Extensions.Logging;
using System.Threading.Tasks;

namespace CloudInstallEngine.Azure.InstallTasks
{
    /// <summary>
    /// Encapsulate a child job to run as a task. No return value.
    /// </summary>
    public class RunChildJobTask : BaseInstallTask
    {
        private readonly SequentialTaskListInstallJob _job;

        public RunChildJobTask(SequentialTaskListInstallJob job, ILogger logger) : base(TaskConfig.NoConfig, logger)
        {
            _job = job;
        }

        public override async Task<object> ExecuteTask(object contextArg)
        {
            // Pass context to child job
            await _job.Install(contextArg);
            return null;
        }
    }

    public class PassResultOnlyTask : BaseInstallTask
    {
        public PassResultOnlyTask(ILogger logger) : base(TaskConfig.NoConfig, logger)
        {
        }

        public override Task<object> ExecuteTask(object contextArg)
        {
            return Task.FromResult(contextArg);
        }
    }
}
