using System;

namespace WebJob.AppInsightsImporter.Engine.ApiImporter
{
    [AttributeUsage(AttributeTargets.All, Inherited = false, AllowMultiple = false)]
    sealed class AppInsightsQueryResultPropertyAttribute : Attribute
    {
        readonly string _propName;
        public AppInsightsQueryResultPropertyAttribute(string propName)
        {
            this._propName = propName;
        }

        public string PropName
        {
            get { return _propName; }
        }
    }
}
