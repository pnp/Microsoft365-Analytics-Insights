using Common.Entities.Agent365;
using DataUtils.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Agent365
{
    public class Agent365PackageCatalogImporter
    {
        private const string CatalogUrl = "https://graph.microsoft.com/v1.0/copilot/admin/catalog/packages";
        private const string RequiredPermission = "CopilotPackages.Read.All";
        private const int MaxPages = 10000;

        private readonly ImportAppIndentityOAuthContext _appIdentity;
        private readonly ManualGraphCallClient _graphClient;
        private readonly IAgent365PackageCatalogStore _store;
        private readonly ILogger _logger;
        private readonly Func<ImportAppIndentityOAuthContext, Task<AppTokenPermissionAccess>> _permissionVerifier;

        public Agent365PackageCatalogImporter(
            ImportAppIndentityOAuthContext appIdentity,
            ManualGraphCallClient graphClient,
            IAgent365PackageCatalogStore store,
            ILogger logger,
            Func<ImportAppIndentityOAuthContext, Task<AppTokenPermissionAccess>> permissionVerifier = null)
        {
            _appIdentity = appIdentity;
            _graphClient = graphClient ?? throw new ArgumentNullException(nameof(graphClient));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _logger = logger;
            _permissionVerifier = permissionVerifier ?? (identity => AppTokenPermissionVerifier.GetAccessAsync(
                identity, new[] { RequiredPermission }, _logger, "the Agent 365 Package Management API permission"));
        }

        public async Task<bool> ImportAsync()
        {
            Guid runId;
            try
            {
                runId = await _store.BeginImportAsync(DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Could not begin the Agent 365 package catalog import ({ex.GetType().Name}).");
                return false;
            }

            try
            {
                var permission = await _permissionVerifier(_appIdentity);
                if (permission != AppTokenPermissionAccess.Granted)
                {
                    var message = permission == AppTokenPermissionAccess.NotGranted
                        ? "The runtime app token is missing the CopilotPackages.Read.All application permission. Grant admin consent and retry."
                        : "The CopilotPackages.Read.All application permission could not be verified. Check the runtime app authentication and importer logs.";
                    await FailAsync(runId, message);
                    _logger?.LogWarning(message);
                    return false;
                }

                var packageCount = 0;
                var elementCount = 0;
                var pageCount = 0;
                var visitedUrls = new HashSet<string>(StringComparer.Ordinal);
                var nextUrl = CatalogUrl;

                while (!string.IsNullOrEmpty(nextUrl))
                {
                    pageCount++;
                    if (pageCount > MaxPages)
                    {
                        throw new InvalidDataException("The catalog exceeded the maximum supported page count.");
                    }

                    ValidateGraphUrl(nextUrl);
                    if (!visitedUrls.Add(nextUrl))
                    {
                        throw new InvalidDataException("The catalog returned a repeated pagination link.");
                    }

                    using (var response = await _graphClient.GetAsyncWithThrottleRetries(nextUrl, _logger))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            var message = response.StatusCode == HttpStatusCode.Forbidden
                                ? "Graph returned HTTP 403. The Package Management API requires a Microsoft Agent 365 license and the CopilotPackages.Read.All application permission with admin consent."
                                : $"Graph returned HTTP {(int)response.StatusCode} while reading the Agent 365 package catalog.";
                            throw new Agent365CatalogGraphException(response.StatusCode, message);
                        }

                        var body = await response.Content.ReadAsStringAsync();
                        var page = ParsePage(body);
                        await _store.SavePageAsync(runId, page.Packages);
                        packageCount += page.Packages.Count;
                        elementCount += page.Packages.Sum(package => package.Elements.Count);
                        nextUrl = page.NextLink;
                    }
                }

                await _store.CompleteImportAsync(runId, DateTime.UtcNow, packageCount, elementCount);
                _logger?.LogInformation($"Imported {packageCount} Agent 365 package(s) across {pageCount} Graph page(s).");
                return true;
            }
            catch (Agent365CatalogGraphException ex)
            {
                await FailAsync(runId, ex.SafeMessage);
                _logger?.LogWarning($"Agent 365 package catalog import failed: {ex.SafeMessage}");
                return false;
            }
            catch (InvalidDataException ex)
            {
                var message = $"The Agent 365 catalog response was invalid: {ex.Message}";
                await FailAsync(runId, message);
                _logger?.LogWarning(message);
                return false;
            }
            catch (HttpRequestException ex)
            {
                var message = $"The Agent 365 catalog Graph request failed: {ex.Message}";
                await FailAsync(runId, message);
                _logger?.LogWarning(message);
                return false;
            }
            catch (Exception ex)
            {
                var message = $"The Agent 365 catalog import failed while reading or saving Graph data ({ex.GetType().Name}).";
                await FailAsync(runId, message);
                _logger?.LogError($"Agent 365 package catalog import failed ({ex.GetType().Name}).");
                return false;
            }
        }

        private Task FailAsync(Guid runId, string message)
        {
            return _store.FailImportAsync(runId, DateTime.UtcNow, message);
        }

        private static void ValidateGraphUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(uri.Host, "graph.microsoft.com", StringComparison.OrdinalIgnoreCase)
                || uri.Port != 443
                || !string.IsNullOrEmpty(uri.UserInfo))
            {
                throw new InvalidDataException("The catalog returned an invalid Graph pagination link.");
            }
        }

        private static Agent365PackagePage ParsePage(string body)
        {
            JObject json;
            try
            {
                using (var stringReader = new StringReader(body))
                using (var jsonReader = new JsonTextReader(stringReader) { DateParseHandling = DateParseHandling.None })
                {
                    json = JObject.Load(jsonReader);
                }
            }
            catch (JsonException)
            {
                throw new InvalidDataException("The Graph catalog response was not valid JSON.");
            }

            if (!(json["value"] is JArray value))
            {
                throw new InvalidDataException("The Graph catalog response did not contain a package collection.");
            }

            var packages = value.OfType<JObject>().Select(ParsePackage).ToList();
            if (packages.Count != value.Count)
            {
                throw new InvalidDataException("The Graph catalog response contained an invalid package.");
            }

            var nextLinkToken = json["@odata.nextLink"];
            if (nextLinkToken != null && nextLinkToken.Type != JTokenType.String && nextLinkToken.Type != JTokenType.Null)
            {
                throw new InvalidDataException("The catalog returned an invalid Graph pagination link.");
            }
            var nextLink = nextLinkToken?.Type == JTokenType.String ? nextLinkToken.Value<string>() : null;
            if (nextLinkToken?.Type == JTokenType.String && string.IsNullOrWhiteSpace(nextLink))
            {
                throw new InvalidDataException("The catalog returned an invalid Graph pagination link.");
            }
            return new Agent365PackagePage { Packages = packages, NextLink = nextLink };
        }

        private static Agent365Package ParsePackage(JObject json)
        {
            var package = new Agent365Package
            {
                PackageId = RequiredString(json, "id", 450),
                AgentIdentityId = OptionalString(json, "agentIdentityId", 450),
                DisplayName = OptionalString(json, "displayName", 1000),
                PackageType = OptionalString(json, "type", 100),
                Platform = OptionalString(json, "platform", 200),
                Publisher = OptionalString(json, "publisher", 1000),
                ManifestId = OptionalString(json, "manifestId", 450),
                Version = OptionalString(json, "version", 100),
                IsBlocked = OptionalBoolean(json, "isBlocked"),
                LastModifiedUtc = OptionalDateTime(json, "lastModifiedDateTime"),
                LastUsedUtc = OptionalDateTime(json, "lastUsedDateTime"),
                LastUsedDateTimeProvided = json.Property("lastUsedDateTime") != null,
                ActiveUsers = OptionalInt(json, "activeUsers"),
                TotalSessions = OptionalInt(json, "totalSessions"),
                TotalRunTimeHours = OptionalDouble(json, "totalRunTimeInHours"),
                ExceptionRate = OptionalDouble(json, "exceptionRate"),
                SupportedHostsJson = SerializeOptionalArray(json, "supportedHosts")
            };

            var elementDetailsToken = json["elementDetails"];
            if (elementDetailsToken != null && elementDetailsToken.Type != JTokenType.Null && !(elementDetailsToken is JArray))
            {
                throw new InvalidDataException("The Graph catalog response contained invalid element details.");
            }

            if (elementDetailsToken is JArray elementDetails)
            {
                foreach (var detail in elementDetails.OfType<JObject>())
                {
                    var elementType = RequiredString(detail, "elementType", 100);
                    if (!(detail["elements"] is JArray elements))
                    {
                        if (detail["elements"] == null || detail["elements"].Type == JTokenType.Null) continue;
                        throw new InvalidDataException("The Graph catalog response contained invalid package elements.");
                    }

                    foreach (var element in elements.OfType<JObject>())
                    {
                        package.Elements.Add(new Agent365PackageElement
                        {
                            ElementType = elementType,
                            ElementId = RequiredString(element, "id", 450)
                        });
                    }

                    if (detail["elements"].Count() != elements.Count)
                    {
                        throw new InvalidDataException("The Graph catalog response contained an invalid package element.");
                    }
                }

                if (elementDetails.Count != elementDetailsToken.Count())
                {
                    throw new InvalidDataException("The Graph catalog response contained invalid element details.");
                }
            }

            return package;
        }

        private static string RequiredString(JObject json, string name, int maximumLength)
        {
            var value = OptionalString(json, name, maximumLength);
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidDataException($"The Graph catalog package did not contain a valid {name}.");
            }

            return value;
        }

        private static string OptionalString(JObject json, string name, int maximumLength)
        {
            var token = json[name];
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }

            if (token.Type != JTokenType.String)
            {
                throw new InvalidDataException("The Graph catalog response contained an invalid text field.");
            }

            var value = token.Value<string>();
            if (value != null && value.Length > maximumLength)
            {
                throw new InvalidDataException("A Graph catalog text field exceeded the supported length.");
            }

            return value;
        }

        private static bool? OptionalBoolean(JObject json, string name)
        {
            var token = json[name];
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token.Type != JTokenType.Boolean) throw new InvalidDataException("The Graph catalog response contained an invalid Boolean field.");
            return token.Value<bool>();
        }

        private static int? OptionalInt(JObject json, string name)
        {
            var token = json[name];
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token.Type != JTokenType.Integer) throw new InvalidDataException("The Graph catalog response contained an invalid integer field.");
            return token.Value<int>();
        }

        private static double? OptionalDouble(JObject json, string name)
        {
            var token = json[name];
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token.Type != JTokenType.Integer && token.Type != JTokenType.Float)
            {
                throw new InvalidDataException("The Graph catalog response contained an invalid numeric field.");
            }

            var value = token.Value<double>();
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                throw new InvalidDataException("The Graph catalog response contained a non-finite numeric field.");
            }

            return value;
        }

        private static DateTime? OptionalDateTime(JObject json, string name)
        {
            var token = json[name];
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token.Type != JTokenType.String
                || !DateTimeOffset.TryParse(token.Value<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value))
            {
                throw new InvalidDataException($"The Graph catalog response contained an invalid {name} date field.");
            }

            return value.UtcDateTime;
        }

        private static string SerializeOptionalArray(JObject json, string name)
        {
            var token = json[name];
            if (token == null || token.Type == JTokenType.Null) return null;
            if (!(token is JArray array) || array.Any(item => item.Type != JTokenType.String))
            {
                throw new InvalidDataException("The Graph catalog response contained an invalid collection field.");
            }

            return array.ToString(Formatting.None);
        }

        private class Agent365PackagePage
        {
            public IReadOnlyList<Agent365Package> Packages { get; set; }
            public string NextLink { get; set; }
        }

        private class Agent365CatalogGraphException : Exception
        {
            public Agent365CatalogGraphException(HttpStatusCode statusCode, string safeMessage)
                : base($"Graph returned HTTP {(int)statusCode}.")
            {
                SafeMessage = safeMessage;
            }

            public string SafeMessage { get; }
        }
    }
}
