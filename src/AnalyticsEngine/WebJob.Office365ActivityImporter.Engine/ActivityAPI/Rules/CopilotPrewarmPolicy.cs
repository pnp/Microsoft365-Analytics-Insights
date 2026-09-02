using ActivityImporter.Engine.ActivityAPI.Copilot;
using System.Collections.Generic;
using System.Linq;
using WebJob.Office365ActivityImporter.Engine.Entities;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;

namespace WebJob.Office365ActivityImporter.Engine.ActivityAPI.Rules
{
    /// <summary>
    /// Decides what the audit-log save path pre-resolves from Graph before it takes the SQL lock: whether to
    /// pre-warm at all, and which file contexts are worth a round-trip.
    ///
    /// Extracted from <c>ActivityReportSqlPersistenceManager</c> so both halves are assertable without Graph
    /// or SQL Server. See issue #373.
    /// </summary>
    public static class CopilotPrewarmPolicy
    {
        /// <summary>
        /// Pre-warm only when there is a run-scoped loader to warm AND Copilot resource resolution is
        /// enabled. The second condition matters operationally: with resolution disabled the save path makes
        /// no Graph resource calls at all (every Copilot event is staged agent-metadata-only), so warming
        /// would be pure outbound Graph traffic for a cache nothing reads. A tenant that has deliberately
        /// turned resolution off must see no Copilot file lookups.
        /// </summary>
        public static bool ShouldPrewarm(bool hasSharedLoader, bool resolveCopilotResourceMetadata)
        {
            return hasSharedLoader && resolveCopilotResourceMetadata;
        }

        /// <summary>
        /// The distinct (fileContextId -> eventUpn) map to pre-resolve for a batch. Mirrors
        /// <c>CopilotAuditEventManager</c>: only the first file-type context per event is used; a Teams meeting
        /// context ends file processing for that event; Teams chat contexts are additive (not files).
        /// </summary>
        public static Dictionary<string, string> ExtractFileContexts(IEnumerable<AbstractAuditLogContent> activities)
        {
            var fileContexts = new Dictionary<string, string>();
            foreach (var copilot in activities.OfType<CopilotAuditLogContent>())
            {
                var contexts = copilot.CopilotEventData?.Contexts;
                if (contexts == null) continue;
                foreach (var context in contexts)
                {
                    if (context == null) continue;
                    // Type is checked before the id guard so a (typically non-null) meeting/chat context
                    // controls flow exactly as CopilotAuditEventManager does, even if its id were null.
                    if (context.Type == ActivityImportConstants.COPILOT_CONTEXT_TYPE_TEAMS_MEETING) break;   // meeting ends file/meeting processing
                    if (context.Type == ActivityImportConstants.COPILOT_CONTEXT_TYPE_TEAMS_CHAT) continue;   // chat is additive, not a file
                    // First file-type context for this event (a null-id file resolves to nothing, so skip it but still stop).
                    // Also skip contexts Graph can never resolve (local C:\ / UNC / DataAgent) so the concurrent
                    // prewarm doesn't fire a guaranteed-miss round-trip for each one (mirrors TryAddFileAsync).
                    if (context.Id != null
                        && !CopilotAuditEventManager.ShouldSkipGraphFileLookup(context.Id)
                        && !fileContexts.ContainsKey(context.Id))
                    {
                        fileContexts[context.Id] = copilot.UserId;
                    }
                    break;
                }
            }
            return fileContexts;
        }
    }
}
