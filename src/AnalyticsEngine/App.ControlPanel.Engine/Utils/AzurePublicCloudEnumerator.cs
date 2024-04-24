using Azure.Core;
using System.Collections.Generic;
using System.Reflection;

namespace App.ControlPanel.Engine
{
    public class AzurePublicCloudEnumerator
    {
        public static List<AzureLocation> GetAzureLocations()
        {
            var list = new List<AzureLocation>();
            var allProps = typeof(AzureLocation).GetProperties(BindingFlags.Public | BindingFlags.Static);

            var obj = new AzureLocation();
            foreach (var prop in allProps)
            {
                if (prop.PropertyType == typeof(AzureLocation))
                {
                    list.Add((AzureLocation)prop.GetValue(obj));
                }

            }
            return list;
        }
    }
}
