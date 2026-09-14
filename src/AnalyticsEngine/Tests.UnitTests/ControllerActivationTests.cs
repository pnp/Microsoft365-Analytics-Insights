extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb.Controllers;
using AnalyticsWeb::Web.AnalyticsWeb.Models.Health;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    [TestClass]
    public class ControllerActivationTests
    {
        public static IEnumerable<object[]> ControllerTypes()
        {
            return typeof(HealthAPIController).Assembly.GetTypes()
                .Where(type => type.IsPublic && !type.IsAbstract && !type.ContainsGenericParameters
                    && typeof(ControllerBase).IsAssignableFrom(type))
                .OrderBy(type => type.FullName)
                .Select(type => new object[] { type });
        }

        [DataTestMethod]
        [DynamicData(nameof(ControllerTypes), DynamicDataSourceType.Method)]
        public void MvcFactory_HasAnUnambiguousConstructor(Type controllerType)
        {
            // MVC creates this factory before running the action. Do not instantiate controllers
            // here: some constructors read application configuration or initialise external services.
            var factory = ActivatorUtilities.CreateFactory(controllerType, Type.EmptyTypes);

            Assert.IsNotNull(factory);
        }

        [TestMethod]
        public void HealthController_MvcActivation_PreservesTheSharedService()
        {
            using var services = new ServiceCollection().BuildServiceProvider();
            var factory = ActivatorUtilities.CreateFactory(typeof(HealthAPIController), Type.EmptyTypes);

            var first = (HealthAPIController)factory(services, Array.Empty<object>());
            var second = (HealthAPIController)factory(services, Array.Empty<object>());

            Assert.AreSame(HealthService.Default, first.Service);
            Assert.AreSame(first.Service, second.Service);
        }

        [TestMethod]
        public async Task UserLookupController_MvcActivation_HandlesInvalidInputWithoutExternalServices()
        {
            using var services = new ServiceCollection().BuildServiceProvider();
            var factory = ActivatorUtilities.CreateFactory(typeof(UserDataLookupAPIController), Type.EmptyTypes);
            var controller = (UserDataLookupAPIController)factory(services, Array.Empty<object>());

            var result = await controller.Summary();

            Assert.IsInstanceOfType(result, typeof(ObjectResult));
            Assert.AreEqual(400, ((ObjectResult)result).StatusCode);
        }
    }
}
