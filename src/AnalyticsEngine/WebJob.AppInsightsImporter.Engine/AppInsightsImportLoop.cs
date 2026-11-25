using DataUtils;
using System;

namespace WebJob.AppInsightsImporter.Engine
{
    public static class AppInsightsImportLoop
    {
        /// <summary>A one-shot invocation reports the cycle's result; the continuous WebJob retries after its normal pause.</summary>
        public static int Run(bool oneShot, Func<AppInsightsCycleOutcome> runCycle, Action pause, Func<bool> shutdownRequested)
        {
            while (!shutdownRequested())
            {
                var outcome = runCycle();
                if (oneShot)
                {
                    return outcome == AppInsightsCycleOutcome.Failed ? HostRuntime.FatalExitCode : 0;
                }
                pause();
            }
            return 0;
        }
    }
}
