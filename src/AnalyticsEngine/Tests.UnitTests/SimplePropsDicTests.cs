using Microsoft.VisualStudio.TestTools.UnitTesting;
using WebJob.AppInsightsImporter.Engine.APIResponseParsers.CustomEvents;

namespace Tests.UnitTests
{
    /// <summary>
    /// Regression coverage for PageUpdateEventCustomProps.SimplePropsDic. The PR #95
    /// fix changed val.Value.ToString() to val.Value?.ToString() ?? string.Empty so
    /// that JSON properties with a literal null value no longer throw a NRE.
    /// </summary>
    [TestClass]
    public class SimplePropsDicTests
    {
        [TestMethod]
        public void SimplePropsDic_EmptyPropsString_ReturnsEmptyDictionary()
        {
            var props = new PageUpdateEventCustomProps { Url = "http://t", PropsString = string.Empty };
            Assert.IsNotNull(props.SimplePropsDic);
            Assert.AreEqual(0, props.SimplePropsDic.Count);
        }

        [TestMethod]
        public void SimplePropsDic_StringValue_IsCapturedAsString()
        {
            var props = new PageUpdateEventCustomProps
            {
                Url = "http://t",
                PropsString = "{\"Author\":\"Sam\"}",
            };

            Assert.IsTrue(props.SimplePropsDic.ContainsKey("Author"));
            Assert.AreEqual("Sam", props.SimplePropsDic["Author"]);
        }

        [TestMethod]
        public void SimplePropsDic_NumericValue_IsCapturedAsString()
        {
            var props = new PageUpdateEventCustomProps
            {
                Url = "http://t",
                PropsString = "{\"Views\":42}",
            };

            Assert.IsTrue(props.SimplePropsDic.ContainsKey("Views"));
            Assert.AreEqual("42", props.SimplePropsDic["Views"]);
        }

        [TestMethod]
        public void SimplePropsDic_NullValue_DoesNotThrowAndIsEmptyString()
        {
            // Regression: prior to the fix this would NRE on val.Value.ToString()
            // when the JSON contained a literal null value.
            var props = new PageUpdateEventCustomProps
            {
                Url = "http://t",
                PropsString = "{\"OptionalProp\":null}",
            };

            Assert.IsTrue(props.SimplePropsDic.ContainsKey("OptionalProp"),
                "Null-valued JSON props should still appear in SimplePropsDic.");
            Assert.AreEqual(string.Empty, props.SimplePropsDic["OptionalProp"],
                "A null JSON value should be represented as an empty string, not throw a NullReferenceException.");
        }

        [TestMethod]
        public void SimplePropsDic_MixedNullAndNonNullValues_AllProcessed()
        {
            var props = new PageUpdateEventCustomProps
            {
                Url = "http://t",
                PropsString = "{\"A\":\"x\",\"B\":null,\"C\":7}",
            };

            Assert.AreEqual("x", props.SimplePropsDic["A"]);
            Assert.AreEqual(string.Empty, props.SimplePropsDic["B"]);
            Assert.AreEqual("7", props.SimplePropsDic["C"]);
        }
    }
}
