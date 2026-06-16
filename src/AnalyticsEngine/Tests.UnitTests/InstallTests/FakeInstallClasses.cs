using CloudInstallEngine;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;

namespace Tests.UnitTests.InstallTests
{
    public class TestInstallParentJob : InstallJobInContainerJob<DummyContainerHost>
    {
        FakeParentInstallTaskWithChildJob t = null;
        public TestInstallParentJob(ILogger logger) : base(logger, new FakeResourceContainerLoader(TaskConfig.NoConfig, logger))
        {
            // Run a task that starts its own job
            t = new FakeParentInstallTaskWithChildJob(TaskConfig.NoConfig, logger);
            AddTask(t);

        }

        public FakeCloudParentResource TaskResult => base.GetTaskResult<FakeCloudParentResource>(t);
    }

    public class TestInstallSubJob : InstallJobInContainerJob<DummyContainerHost>
    {
        FakeChildInstallTask1 t1 = null;
        FakeChildInstallTask2 t2 = null;
        public TestInstallSubJob(ILogger logger) : base(logger, new FakeResourceContainerLoader(TaskConfig.NoConfig, logger))
        {
            // Run task 1 alone
            AddTask(new FakeChildInstallTask1(TaskConfig.NoConfig, logger));

            // Run task 1 then 2, passing 1's result to 2
            t1 = new FakeChildInstallTask1(TaskConfig.NoConfig, logger);
            t2 = new FakeChildInstallTask2(TaskConfig.NoConfig, logger);

            AddTask(t1, t2);
        }

        public FakeCloudResourceType1 Task1Result => base.GetTaskResult<FakeCloudResourceType1>(t1);
        public FakeCloudResourceType2 Task2Result => base.GetTaskResult<FakeCloudResourceType2>(t2);
    }


    public class FakeChildInstallTask1 : ResourceInstallTaskInContainer<FakeCloudResourceType1, DummyContainerHost>
    {
        public FakeChildInstallTask1(TaskConfig config, ILogger logger) : base(config, logger) { }

        public override string TaskName => "Test1";
        public override Task<FakeCloudResourceType1> ExecuteTaskReturnResult(object contextArg)
        {
            _logger.LogInformation($"Running task 1 in container {this.Container.RandomId}.");
            return Task.FromResult(new FakeCloudResourceType1());
        }
    }
    public class FakeChildInstallTask2 : ResourceInstallTaskInContainer<FakeCloudResourceType2, DummyContainerHost>
    {
        public FakeChildInstallTask2(TaskConfig config, ILogger logger) : base(config, logger) { }

        public override string TaskName => "Test2";

        public override Task<FakeCloudResourceType2> ExecuteTaskReturnResult(object contextArg)
        {
            _logger.LogInformation($"Running task 2 with result '{contextArg.GetType().Name}' from task1.");
            return Task.FromResult(new FakeCloudResourceType2());
        }
    }

    public class FakeParentInstallTaskWithChildJob : ResourceInstallTaskInContainer<FakeCloudParentResource, DummyContainerHost>
    {
        public FakeParentInstallTaskWithChildJob(TaskConfig config, ILogger logger) : base(config, logger)
        {
        }

        public override async Task<FakeCloudParentResource> ExecuteTaskReturnResult(object contextArg)
        {
            var subJob = new TestInstallSubJob(_logger);
            await subJob.Install();

            return new FakeCloudParentResource { FakeCloudResourceType1 = subJob.Task1Result, FakeCloudResourceType2 = subJob.Task2Result };
        }
    }

    // Return types from tasks
    public class FakeCloudResourceType1 { }
    public class FakeCloudResourceType2 { }
    public class FakeCloudParentResource
    {
        public FakeCloudResourceType1 FakeCloudResourceType1 { get; set; }
        public FakeCloudResourceType2 FakeCloudResourceType2 { get; set; }
    }


    public class FakeResourceContainerLoader : ResourceInstallTask<DummyContainerHost>
    {
        public FakeResourceContainerLoader(TaskConfig config, ILogger logger) : base(config, logger)
        {
        }

        public override string TaskName => "Fake container creator";

        public override Task<DummyContainerHost> ExecuteTaskReturnResult(object contextArg)
        {
            return Task.FromResult(new DummyContainerHost());
        }
    }

    public class DummyContainerHost
    {
        public DummyContainerHost()
        {
            RandomId = Guid.NewGuid();
        }

        // Read a prop to check we have a not-null container in tasks
        public Guid RandomId { get; set; }
    }

    /// <summary>Concrete <see cref="SequentialTaskListInstallJob"/> so the task-loop can be unit-tested directly.</summary>
    public class FakeSequentialJob : SequentialTaskListInstallJob
    {
        public FakeSequentialJob(ILogger logger) : base(logger) { }
    }

    /// <summary>Task that always throws. <see cref="IsCritical"/> is configurable to test abort-vs-continue behaviour.</summary>
    public class FakeThrowingTask : BaseInstallTask
    {
        private readonly bool _isCritical;
        public bool WasRun { get; private set; }

        public FakeThrowingTask(TaskConfig config, ILogger logger, bool isCritical) : base(config, logger)
        {
            _isCritical = isCritical;
        }

        public override bool IsCritical => _isCritical;

        public override Task<object> ExecuteTask(object contextArg)
        {
            WasRun = true;
            throw new InvalidOperationException("Simulated task failure for testing");
        }
    }

    /// <summary>Task that records whether it ran, used to assert downstream tasks still execute (or not).</summary>
    public class FakeMarkerTask : BaseInstallTask
    {
        public bool WasRun { get; private set; }

        public FakeMarkerTask(TaskConfig config, ILogger logger) : base(config, logger) { }

        public override Task<object> ExecuteTask(object contextArg)
        {
            WasRun = true;
            return Task.FromResult<object>(new object());
        }
    }

    /// <summary>Task that returns a caller-supplied result object, used to test result carry-forward.</summary>
    public class FakeResultTask : BaseInstallTask
    {
        private readonly object _result;

        public FakeResultTask(TaskConfig config, ILogger logger, object result) : base(config, logger)
        {
            _result = result;
        }

        public override Task<object> ExecuteTask(object contextArg)
        {
            return Task.FromResult(_result);
        }
    }

    /// <summary>Task that records the context argument it received, used to assert what was carried forward.</summary>
    public class FakeContextCapturingTask : BaseInstallTask
    {
        public bool WasRun { get; private set; }
        public object ReceivedContext { get; private set; }

        public FakeContextCapturingTask(TaskConfig config, ILogger logger) : base(config, logger) { }

        public override Task<object> ExecuteTask(object contextArg)
        {
            WasRun = true;
            ReceivedContext = contextArg;
            return Task.FromResult<object>(new object());
        }
    }
}
