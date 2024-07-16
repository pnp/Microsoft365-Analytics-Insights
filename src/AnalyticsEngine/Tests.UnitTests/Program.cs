using Common.Entities.Config;
using DataUtils;
using Microsoft.ApplicationInsights;
using System;
using System.Threading.Tasks;
using Tests.UnitTests.FakeLoaderClasses;
using WebJob.Office365ActivityImporter.Engine;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI;

namespace Tests.UnitTests
{
    public class Program
    {
        private static void Main()
        {
            var config = new AppConfig();
            var appInsights = new TelemetryClient(new Microsoft.ApplicationInsights.Extensibility.TelemetryConfiguration()
            {
                ConnectionString = $"InstrumentationKey={config.AppInsightsConnectionString}"
            });

            var startDate = DateTime.Now;

            Task.Run(() => DoThing()).Wait();

            Console.WriteLine($"\nDone and that.");
            Console.ReadKey();
        }

        private static async Task DoThing()
        {
            var telemetry = AnalyticsLogger.ConsoleOnlyTracer();

            var testLoader = new FakeActivityImporter(100, new AppConfig(), telemetry);

            var t = new JobTimer(telemetry, "Soyve");
            t.Start();
            Console.WriteLine("Saving data...");
            await testLoader.LoadReportsAndSave(new ActivityReportSqlPersistenceManager(new AllowAllFilterConfig(), telemetry, new AppConfig()));

            t.StopAndPrintElapsed();
        }
    }
}
