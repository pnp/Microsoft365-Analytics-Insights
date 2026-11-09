extern alias AnalyticsWeb;

using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Tests.UnitTests
{
    /// <summary>
    /// The few MVC views the web app still has must render: an action that returns its own view has one, a view's model
    /// type exists, and nothing uses ASP.NET Core syntax that MVC 5 sends to the browser as it is (#853, R6-10).
    /// </summary>
    [TestClass]
    public class MvcViewTests
    {
        private static readonly Regex ViewModelDirective = new Regex(@"^\s*@model\s+([\w.]+)", RegexOptions.Multiline);

        [TestMethod]
        public void EveryActionThatReturnsItsOwnView_HasOne()
        {
            var missing = new List<string>();
            foreach (var controllerFile in Directory.GetFiles(Path.Combine(WebDirectory(), "Controllers"), "*Controller.cs"))
            {
                var source = File.ReadAllText(controllerFile);
                var controller = Path.GetFileNameWithoutExtension(controllerFile);
                if (!Regex.IsMatch(source, $@"class\s+{controller}\s*:\s*Controller\b")) continue;

                foreach (var action in ActionsReturningTheirOwnView(source))
                {
                    var name = controller.Substring(0, controller.Length - "Controller".Length);
                    if (!File.Exists(Path.Combine(WebDirectory(), "Views", name, action + ".cshtml"))
                        && !File.Exists(Path.Combine(WebDirectory(), "Views", "Shared", action + ".cshtml")))
                    {
                        missing.Add($"{controller}.{action}");
                    }
                }
            }

            Assert.AreEqual(0, missing.Count, "These actions return a view that doesn't exist, so they answer 500: " + string.Join(", ", missing));
        }

        [TestMethod]
        public void EveryViewsModelType_Exists()
        {
            var web = typeof(AnalyticsWeb::Web.AnalyticsWeb.Security.PortalPermission).Assembly;
            var assemblies = new[] { web }.Concat(web.GetReferencedAssemblies().Select(TryLoad).Where(a => a != null)).ToList();

            var unresolved = Views()
                .SelectMany(v => ViewModelDirective.Matches(File.ReadAllText(v)).Cast<Match>().Select(m => new { View = v, Type = m.Groups[1].Value }))
                .Where(m => assemblies.All(a => a.GetType(m.Type) == null))
                .Select(m => $"{Path.GetFileName(m.View)}: {m.Type}")
                .ToList();

            Assert.AreEqual(0, unresolved.Count, "Views bound to a model type that doesn't exist: " + string.Join(", ", unresolved));
        }

        [TestMethod]
        public void NoView_UsesAnAspNetCoreTagHelper()
        {
            var offenders = Views().Where(v => Regex.IsMatch(File.ReadAllText(v), @"\sasp-[\w-]+\s*=")).Select(Path.GetFileName).ToList();

            Assert.AreEqual(0, offenders.Count, "MVC 5 renders asp-* attributes as they are, so the link goes nowhere: " + string.Join(", ", offenders));
        }

        [TestMethod]
        public void EveryViewIsPublished_AndEveryPublishedViewExists()
        {
            var project = File.ReadAllText(Path.Combine(WebDirectory(), "Web.csproj"));
            var published = Regex.Matches(project, @"<Content Include=""(Views\\[^""]+\.cshtml)""").Cast<Match>()
                .Select(m => m.Groups[1].Value).ToList();
            var onDisk = Views().Select(v => v.Substring(WebDirectory().Length + 1)).ToList();

            CollectionAssert.AreEquivalent(onDisk, published, "Web.csproj's Content items and the Views folder disagree.");
        }

        /// <summary>The names of public <c>ActionResult</c> methods whose body has <c>return View();</c>.</summary>
        private static IEnumerable<string> ActionsReturningTheirOwnView(string source)
        {
            foreach (Match match in Regex.Matches(source, @"public\s+ActionResult\s+(\w+)\s*\([^)]*\)\s*\{"))
            {
                var depth = 1;
                var i = match.Index + match.Length;
                var start = i;
                while (i < source.Length && depth > 0)
                {
                    if (source[i] == '{') depth++;
                    else if (source[i] == '}') depth--;
                    i++;
                }

                if (Regex.IsMatch(source.Substring(start, i - start), @"\bView\(\s*\)"))
                {
                    yield return match.Groups[1].Value;
                }
            }
        }

        private static IEnumerable<string> Views() =>
            Directory.GetFiles(Path.Combine(WebDirectory(), "Views"), "*.cshtml", SearchOption.AllDirectories);

        private static Assembly TryLoad(AssemblyName name)
        {
            try
            {
                return Assembly.Load(name);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string WebDirectory()
        {
            for (var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory); directory != null; directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "Web");
                if (File.Exists(Path.Combine(candidate, "Web.csproj"))) return candidate;
            }

            Assert.Fail("Web\\Web.csproj was not found above " + AppDomain.CurrentDomain.BaseDirectory);
            return null;
        }
    }
}
