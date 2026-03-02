using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Common.Entities.Config;
using Microsoft.Extensions.Logging;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI;
using WebJob.Office365ActivityImporter.Engine.Engine.Entities;

namespace Tests.StressTesting.FakeLoaders
{
    /// <summary>
    /// Fake content metadata loader for stress testing
    /// </summary>
    public class FakeContentMetaDataLoaderForStress : ContentMetaDataLoader<ActivityReportInfo>
    {
        private readonly int _reportsPerTimeSlot;
        private readonly int _timeSlotCount;

        public FakeContentMetaDataLoaderForStress(int reportsPerTimeSlot, int timeSlotCount) 
            : base(new FakeLogger(), FakeAppConfigFactory.Create())
        {
            _reportsPerTimeSlot = reportsPerTimeSlot;
            _timeSlotCount = timeSlotCount;
        }

        public new List<TimePeriod> GetScanningTimeChunksFromNow()
        {
            var chunks = new List<TimePeriod>();
            var now = DateTime.UtcNow;

            for (int i = 0; i < _timeSlotCount; i++)
            {
                var end = now.AddHours(-i * 1);
                var start = end.AddHours(-1);
                chunks.Add(new TimePeriod(start, end));
            }

            return chunks;
        }

        protected override async Task<List<ActivityReportInfo>> LoadAllActivityReports(string auditContentType, TimePeriod chunk, int batchId)
        {
            var metadata = new List<ActivityReportInfo>();

            for (int i = 0; i < _reportsPerTimeSlot; i++)
            {
                metadata.Add(new ActivityReportInfo
                {
                    ContentUri = new Uri($"https://fake.office.com/report/{Guid.NewGuid()}"),
                    ContentId = Guid.NewGuid().ToString(),
                    ContentType = auditContentType,
                    BatchID = batchId
                });
            }

            return await Task.FromResult(metadata);
        }
    }

    /// <summary>
    /// Fake logger for stress testing that does nothing
    /// </summary>
    internal class FakeLogger : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) => null;
        public bool IsEnabled(LogLevel logLevel) => false;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter) { }
    }
}
