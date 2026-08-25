using Common.Entities;
using Common.Entities.Entities.AuditLog;
using System;
using System.Linq;

namespace Tests.FakeDataGen.Copilot
{
    /// <summary>
    /// Generates accessed resources for custom Copilot agent events
    /// </summary>
    public class CopilotResourceGenerator
    {
        private readonly Random _random;

        public CopilotResourceGenerator(Random random)
        {
            _random = random;
        }

        /// <summary>
        /// Adds 2-3 accessed resources to a custom agent event
        /// </summary>
        public void AddAccessedResources(AnalyticsEntitiesContext db, CopilotChat copilotChat)
        {
            // Add 2-3 accessed resources per custom agent event
            int resourceCount = _random.Next(2, 4); // 2 or 3 resources

            for (int i = 0; i < resourceCount; i++)
            {
                var resource = new CopilotEventAccessedResource
                {
                    ChatId = copilotChat.EventID,
                    RelatedChat = copilotChat
                };

                // Randomly decide the type of resource
                int resourceType = _random.Next(3);

                if (resourceType == 0)
                {
                    AddSharePointResource(db, resource);
                }
                else if (resourceType == 1)
                {
                    AddOutlookResource(db, resource);
                }
                else
                {
                    AddWebPageResource(db, resource);
                }

                db.CopilotEventAccessedResources.Add(resource);
            }
        }

        /// <summary>
        /// Sets the action Copilot performed and the SharePoint list item id behind the resource.
        /// </summary>
        /// <remarks>
        /// <paramref name="listItemSourceId"/> is deliberately the SAME lookup row as the resource id most of
        /// the time. The audit payload very often repeats the resource Id verbatim as listItemUniqueId, and
        /// both columns are dimensioned against <c>copilot_event_accessed_resource_ids</c> precisely so the
        /// two de-duplicate against each other instead of storing the string twice on the largest Copilot
        /// table. Generating a distinct value every time would hide that, and would inflate the dimension in
        /// a way real data does not.
        /// </remarks>
        private void AddActionAndListItem(AnalyticsEntitiesContext db, CopilotEventAccessedResource resource, bool sharePointBacked)
        {
            resource.Action = GetOrCreateAccessedResourceAction(db,
                CopilotActivityGeneratorConfig.AccessedResourceActions[
                    _random.Next(CopilotActivityGeneratorConfig.AccessedResourceActions.Length)]);

            if (!sharePointBacked)
                return;

            resource.ListItemUniqueId = _random.Next(100) < 80
                ? resource.ResourceId
                : GetOrCreateAccessedResourceId(db, Guid.NewGuid().ToString().ToUpperInvariant());
        }

        private CopilotAccessedResourceAction GetOrCreateAccessedResourceAction(AnalyticsEntitiesContext db, string name)
        {
            var action = db.CopilotAccessedResourceActions.Local.FirstOrDefault(a => a.Name == name)
                ?? db.CopilotAccessedResourceActions.FirstOrDefault(a => a.Name == name);

            if (action == null)
            {
                action = new CopilotAccessedResourceAction { Name = name };
                db.CopilotAccessedResourceActions.Add(action);
            }

            return action;
        }

        private void AddSharePointResource(AnalyticsEntitiesContext db, CopilotEventAccessedResource resource)
        {
            // SharePoint/OneDrive document
            string docName = CopilotActivityGeneratorConfig.ResourceDocumentNames[_random.Next(CopilotActivityGeneratorConfig.ResourceDocumentNames.Length)];
            string siteUrl = CopilotActivityGeneratorConfig.ResourceSiteUrls[_random.Next(CopilotActivityGeneratorConfig.ResourceSiteUrls.Length - 1)]; // Exclude Outlook from site URLs for docs
            string docGuid = Guid.NewGuid().ToString().ToUpper();
            string resourceId = $"{siteUrl}/_layouts/15/Doc.aspx?sourcedoc=%7B{docGuid}%7D&file={docName}&action=default&mobileredirect=true&DefaultItemOpen=1";

            resource.ResourceId = GetOrCreateAccessedResourceId(db, resourceId);
            resource.ResourceName = GetOrCreateAccessedResourceName(db, docName);
            resource.ResourceSiteUrl = GetOrCreateAccessedResourceSiteUrl(db, siteUrl);
            resource.ResourceType = GetOrCreateAccessedResourceType(db, "File");
            AddActionAndListItem(db, resource, sharePointBacked: true);
        }

        private void AddOutlookResource(AnalyticsEntitiesContext db, CopilotEventAccessedResource resource)
        {
            // Outlook email attachment
            string attachmentName = CopilotActivityGeneratorConfig.ResourceDocumentNames[_random.Next(CopilotActivityGeneratorConfig.ResourceDocumentNames.Length)];
            string attachmentId = Guid.NewGuid().ToString().Replace("-", "").Substring(0, 16);
            string itemId = Guid.NewGuid().ToString().Replace("-", "").Substring(0, 16);
            string resourceId = $"https://outlook.office.com/owa/?viewmodel=IAttachmentViewModelPopoutFactory&AttachmentId=AAkALgAAAAAAHYQDEapmRIAEAAj0pccL7oXSYZRp0Gd14Ev&ItemId=AAkALgAAAACqAC-EWg0A6MwIQliQr0ipIo7o8RbHrQAHfCxWygAA&AttachmentName={attachmentName}&web=1";

            resource.ResourceId = GetOrCreateAccessedResourceId(db, resourceId);
            resource.ResourceName = GetOrCreateAccessedResourceName(db, attachmentName);
            resource.ResourceSiteUrl = GetOrCreateAccessedResourceSiteUrl(db, "https://outlook.office365.com/owa");
            resource.ResourceType = GetOrCreateAccessedResourceType(db, "Email");
            AddActionAndListItem(db, resource, sharePointBacked: false);
        }

        private void AddWebPageResource(AnalyticsEntitiesContext db, CopilotEventAccessedResource resource)
        {
            // Web page or other resource
            string webUrl = CopilotActivityGeneratorConfig.ResourceSiteUrls[_random.Next(CopilotActivityGeneratorConfig.ResourceSiteUrls.Length)];
            string resourceName = webUrl.Contains("accuweather") ? "Weather Forecast - São Tomé" : "Email Message";

            resource.ResourceId = GetOrCreateAccessedResourceId(db, webUrl);
            resource.ResourceName = GetOrCreateAccessedResourceName(db, resourceName);
            resource.ResourceSiteUrl = GetOrCreateAccessedResourceSiteUrl(db, webUrl);
            resource.ResourceType = GetOrCreateAccessedResourceType(db, "WebPage");
        }

        private CopilotAccessedResourceId GetOrCreateAccessedResourceId(AnalyticsEntitiesContext db, string resourceId)
        {
            // Check both database and local context for existing resource
            var resource = db.CopilotAccessedResourceIds.Local.FirstOrDefault(r => r.ResourceId == resourceId);
            if (resource == null)
            {
                resource = db.CopilotAccessedResourceIds.FirstOrDefault(r => r.ResourceId == resourceId);
            }

            if (resource == null)
            {
                resource = new CopilotAccessedResourceId { ResourceId = resourceId };
                db.CopilotAccessedResourceIds.Add(resource);
            }
            return resource;
        }

        private CopilotAccessedResourceName GetOrCreateAccessedResourceName(AnalyticsEntitiesContext db, string name)
        {
            // Check both database and local context for existing resource
            var resource = db.CopilotAccessedResourceNames.Local.FirstOrDefault(r => r.Name == name);
            if (resource == null)
            {
                resource = db.CopilotAccessedResourceNames.FirstOrDefault(r => r.Name == name);
            }

            if (resource == null)
            {
                resource = new CopilotAccessedResourceName { Name = name };
                db.CopilotAccessedResourceNames.Add(resource);
            }
            return resource;
        }

        private CopilotAccessedResourceSiteUrl GetOrCreateAccessedResourceSiteUrl(AnalyticsEntitiesContext db, string siteUrl)
        {
            // Check both database and local context for existing resource
            var resource = db.CopilotAccessedResourceSiteUrls.Local.FirstOrDefault(r => r.SiteUrl == siteUrl);
            if (resource == null)
            {
                resource = db.CopilotAccessedResourceSiteUrls.FirstOrDefault(r => r.SiteUrl == siteUrl);
            }

            if (resource == null)
            {
                resource = new CopilotAccessedResourceSiteUrl { SiteUrl = siteUrl };
                db.CopilotAccessedResourceSiteUrls.Add(resource);
            }
            return resource;
        }

        private CopilotAccessedResourceType GetOrCreateAccessedResourceType(AnalyticsEntitiesContext db, string typeName)
        {
            // Check both database and local context for existing resource type
            var resourceType = db.CopilotAccessedResourceTypes.Local.FirstOrDefault(r => r.Name == typeName);
            if (resourceType == null)
            {
                resourceType = db.CopilotAccessedResourceTypes.FirstOrDefault(r => r.Name == typeName);
            }

            if (resourceType == null)
            {
                resourceType = new CopilotAccessedResourceType { Name = typeName };
                db.CopilotAccessedResourceTypes.Add(resourceType);
            }
            return resourceType;
        }
    }
}
