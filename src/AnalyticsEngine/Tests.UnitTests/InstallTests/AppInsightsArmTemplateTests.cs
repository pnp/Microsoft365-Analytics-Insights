using CloudInstallEngine.Azure.InstallTasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Tests.UnitTests.InstallTests
{
    [TestClass]
    public class AppInsightsArmTemplateTests
    {
        [TestMethod]
        public void TemplateReferencesOnlyDeclaredParameters()
        {
            var resourcesType = typeof(AppInsightsInstallTask).Assembly.GetType(
                "CloudInstallEngine.Properties.Resources",
                throwOnError: true);
            var templateProperty = resourcesType.GetProperty(
                "AppInsightsArmTemplate",
                BindingFlags.Static | BindingFlags.NonPublic);
            var template = (string)templateProperty.GetValue(null);
            var templateJson = JObject.Parse(template);
            var declaredParameters = templateJson["parameters"]
                .Children<JProperty>()
                .Select(parameter => parameter.Name)
                .ToHashSet();
            var referencedParameters = Regex.Matches(template, @"parameters\('([^']+)'\)")
                .Cast<Match>()
                .Select(match => match.Groups[1].Value)
                .Distinct();

            CollectionAssert.IsSubsetOf(
                referencedParameters.ToList(),
                declaredParameters.ToList(),
                "The ARM template references a parameter that is not declared.");
        }
    }
}
