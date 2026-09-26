using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    public static class GraphEndpointTemplate
    {
        private static readonly Regex FunctionRegex = new Regex(
            "^(?<name>[A-Za-z][A-Za-z0-9_.]*)\\(",
            RegexOptions.Compiled);

        private static readonly HashSet<string> LiteralSegments = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "$count",
            "$ref",
            "$value",
            "acceptedSenders",
            "accessReviews",
            "activities",
            "appRoleAssignedTo",
            "applications",
            "attachments",
            "auditLogs",
            "beta",
            "calendar",
            "calendarView",
            "callRecords",
            "channels",
            "chats",
            "children",
            "columns",
            "communications",
            "content",
            "delta",
            "deletedItems",
            "directoryAudits",
            "drive",
            "drives",
            "events",
            "groups",
            "installedApps",
            "items",
            "joinedTeams",
            "lists",
            "mailFolders",
            "members",
            "messages",
            "owners",
            "pages",
            "permissions",
            "planner",
            "presence",
            "rejectedSenders",
            "replies",
            "reports",
            "root",
            "sentitems",
            "servicePrincipals",
            "settings",
            "shares",
            "sites",
            "subscriptions",
            "teams",
            "teamwork",
            "transitiveMembers",
            "users",
            "v1.0"
        };

        public static string FromUrl(string httpMethod, string url)
        {
            var method = string.IsNullOrWhiteSpace(httpMethod) ? "GET" : httpMethod.Trim().ToUpperInvariant();
            return method + " " + TemplatePath(url);
        }

        public static string TemplatePath(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return "/";
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) && !Uri.TryCreate("https://graph.microsoft.com" + url, UriKind.Absolute, out uri))
            {
                return "/{id}";
            }

            var path = uri.AbsolutePath;
            var hadTrailingSlash = path.EndsWith("/", StringComparison.Ordinal) && path.Length > 1;
            var templatedSegments = new List<string>();
            var inColonDelimitedPath = false;
            foreach (var segment in path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var decoded = Uri.UnescapeDataString(segment);
                if (inColonDelimitedPath)
                {
                    templatedSegments.Add("{id}");
                    if (decoded.EndsWith(":", StringComparison.Ordinal))
                    {
                        inColonDelimitedPath = false;
                    }
                    continue;
                }

                templatedSegments.Add(TemplateSegment(decoded));
                if (decoded.EndsWith(":", StringComparison.Ordinal))
                {
                    inColonDelimitedPath = true;
                }
            }

            var templatedPath = "/" + string.Join("/", templatedSegments);
            return hadTrailingSlash ? templatedPath + "/" : templatedPath;
        }

        private static string TemplateSegment(string decoded)
        {
            if (string.IsNullOrWhiteSpace(decoded))
            {
                return "{id}";
            }

            var function = FunctionRegex.Match(decoded);
            if (function.Success)
            {
                return function.Groups["name"].Value + "(...)";
            }

            if (LiteralSegments.Contains(decoded))
            {
                return decoded;
            }

            return "{id}";
        }
    }
}
