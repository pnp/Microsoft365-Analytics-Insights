using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Reflection;
using WebJob.AppInsightsImporter.Engine.ApiImporter;

namespace WebJob.AppInsightsImporter.Engine
{
    public interface ITimeStampResult
    {
        DateTime Timestamp { get; }
    }

    public abstract class BaseAppInsightsQueryResult : ITimeStampResult
    {
        public BaseAppInsightsQueryResult()
        {
        }
        public BaseAppInsightsQueryResult(List<object> rowColumnVals, Dictionary<int, PropertyInfo> propDic)
        {
            // Set this obj using property info found
            foreach (var settableProp in propDic)
            {
                var valToSet = rowColumnVals[settableProp.Key];
                settableProp.Value.SetValue(this, valToSet);
            }
        }

        [AppInsightsQueryResultProperty("timestamp")]
        public DateTime AppInsightsTimestamp { get; set; }

        /// <summary>
        /// Use event timestamp by default. Concrete classes can override this with custom event data.
        /// </summary>
        public virtual DateTime Timestamp => AppInsightsTimestamp;

        [AppInsightsQueryResultProperty("customDimensions")]
        public string CustomDimensionsJson { get; set; }

        [AppInsightsQueryResultProperty("user_AuthenticatedId")]
        public string Username { get; set; }
    }

    public abstract class AppInsightsQueryResultCollection<T> where T : BaseAppInsightsQueryResult
    {
        protected readonly ILogger _debugTracer;

        protected abstract T Build(List<object> rowColumnVals, Dictionary<int, PropertyInfo> propDic);

        public AppInsightsQueryResultCollection() { }

        /// <summary>
        /// Construct from AppInsights table, which is a 2d collection of rows & columns, into a typed collection of objects of T
        /// </summary>
        protected AppInsightsQueryResultCollection(AppInsightsTable fromTable, DateTime fromWhen, ILogger debugTracer)
        {
            _debugTracer = debugTracer;

            // Build column map of properties we can set
            var propDic = new Dictionary<int, PropertyInfo>();
            var propsInfo = typeof(T).GetProperties();

            int colIdx = 0;
            foreach (var col in fromTable.Columns)
            {
                foreach (var pInfo in propsInfo)
                {
                    foreach (var attObj in pInfo.GetCustomAttributes(true))
                    {
                        if (attObj is AppInsightsQueryResultPropertyAttribute)
                        {
                            var att = (AppInsightsQueryResultPropertyAttribute)attObj;
                            if (att.PropName == col.Name)
                            {
                                // Remember column index of the matched property
                                propDic.Add(colIdx, pInfo);
                            }
                        }
                    }
                }
                colIdx++;
            }

            foreach (var row in fromTable.Rows)
            {
                T record = default;
                try
                {
                    record = Build(row, propDic);
                }
                catch (Exception)
                {
                    // We'll just ignore I guess
                }
                if (record != null)
                {
                    // Stats
                    StatsCheck(record);

                    // Only add results that are newer
                    if (record.Timestamp >= fromWhen)
                    {
                        this.Rows.Add(record);
                    }
                }
            }
        }

        #region Props

        public List<T> Rows { get; set; } = new List<T>();

        public DateTime? EarliestRead { get; internal set; }
        public DateTime? LatestRead { get; internal set; }

        #endregion

        protected void StatsCheck(ITimeStampResult result)
        {
            // Stats
            if (!EarliestRead.HasValue || EarliestRead.Value > result.Timestamp)
            {
                EarliestRead = result.Timestamp;
            }
            if (!LatestRead.HasValue || LatestRead.Value < result.Timestamp)
            {
                LatestRead = result.Timestamp;
            }
        }
    }
}
