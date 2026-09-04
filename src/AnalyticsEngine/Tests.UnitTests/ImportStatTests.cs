using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;
using System.Reflection;
using WebJob.Office365ActivityImporter.Engine.Entities;

namespace Tests.UnitTests
{
    [TestClass]
    public class ImportStatTests
    {
        [TestMethod]
        public void AddStats_AccumulatesEveryNumericCounter()
        {
            var first = CreatePopulatedStats(10);
            var second = CreatePopulatedStats(100);
            var total = new ImportStat();

            total.AddStats(first);
            total.AddStats(second);

            foreach (var property in AccumulatedNumericProperties())
            {
                var expected = Convert.ToDouble(property.GetValue(first)) + Convert.ToDouble(property.GetValue(second));
                var actual = Convert.ToDouble(property.GetValue(total));
                Assert.AreEqual(expected, actual, 0.0001,
                    $"ImportStat.AddStats must accumulate {property.Name}; update this test if a future numeric property is intentionally not additive.");
            }
        }

        private static ImportStat CreatePopulatedStats(int seed)
        {
            var stat = new ImportStat();
            int offset = 1;
            foreach (var property in AccumulatedNumericProperties())
            {
                if (property.PropertyType == typeof(int))
                {
                    property.SetValue(stat, seed + offset);
                }
                else if (property.PropertyType == typeof(double))
                {
                    property.SetValue(stat, seed + offset + 0.25);
                }
                offset++;
            }
            return stat;
        }

        private static PropertyInfo[] AccumulatedNumericProperties()
        {
            return typeof(ImportStat)
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(p => p.CanRead && p.CanWrite && (p.PropertyType == typeof(int) || p.PropertyType == typeof(double)))
                .OrderBy(p => p.Name)
                .ToArray();
        }
    }
}
