using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities.Entities.AuditLog
{

    /// <summary>
    /// A copilot interaction event. May not be related to any file or meeting. 
    /// Relates back to a common audit event.
    /// </summary>
    [Table("copilot_chats")]
    public class CopilotChat : BaseOfficeEvent
    {
        [Column("app_host")]
        public string AppHost { get; set; } = null;


        [ForeignKey(nameof(Agent))]
        [Column("agent_id")]
        public int? AgentId { get; set; }
        public CopilotAgent Agent { get; set; } = null;

        /// <summary>
        /// Estimated total Copilot Credits consumed for this interaction.
        /// Calculated from CopilotCreditEstimation in CopilotAuditLogContent.
        /// </summary>
        [Column("copilot_credit_estimate_total")]
        public int? CopilotCreditEstimateTotal { get; set; }

        /// <summary>
        /// JSON-serialized Copilot Credit estimation details from CopilotCreditEstimation.
        /// Contains breakdown of generative answers, tenant graph grounding, deep reasoning, etc.
        /// </summary>
        [Column("copilot_credit_estimate_json")]
        public string CopilotCreditEstimateJson { get; set; } = null;

        /// <summary>
        /// Identifier of the Copilot conversation thread this interaction belongs to
        /// (CopilotEventData.ThreadId). Lets interactions be grouped into conversations and is the
        /// join key to the AI Interaction History sessionId should that ever be imported.
        /// Bounded at 450 chars so it stays within the 1700-byte index-key limit as nvarchar
        /// (2 bytes/char) if a conversation-level index is ever justified by a report.
        /// </summary>
        [Column("thread_id")]
        [MaxLength(450)]
        public string ThreadId { get; set; } = null;

        /// <summary>
        /// The user's region when the interaction happened (audit record ClientRegion, e.g. "US").
        /// </summary>
        [Column("client_region")]
        [MaxLength(50)]
        public string ClientRegion { get; set; } = null;

        /// <summary>
        /// Version of the Copilot audit log schema this record was emitted with
        /// (audit record CopilotLogVersion). Useful when Microsoft changes the payload shape.
        /// </summary>
        [Column("copilot_log_version")]
        [MaxLength(50)]
        public string CopilotLogVersion { get; set; } = null;

        /// <summary>
        /// Denormalised copy of the parent audit event's <c>user_id</c>. See <see cref="TimeStampUtc"/>
        /// for why this is duplicated rather than read through <see cref="BaseOfficeEvent.AuditEvent"/>.
        /// </summary>
        [Column("user_id")]
        public int? UserId { get; set; }

        /// <summary>
        /// Denormalised copy of the parent audit event's <c>time_stamp</c>.
        ///
        /// <para>
        /// A Copilot interaction has no date of its own: the timestamp and the user live on
        /// <c>dbo.audit_events</c>. Every Copilot report therefore used to join
        /// <c>copilot_chats -&gt; audit_events</c>. Carrying the two values here removes that join.
        /// </para>
        /// <para>
        /// Measured on a synthetic bench sized for a large tenant (~10M audit_events at ~1.7 KB/row, ~6M
        /// copilot_chats, Copilot a large share of them): <c>LicensedUsers</c> at a 28-day window went
        /// 13.0s -&gt; 5.6s.
        /// The duplication is structural rather than a tuning shortcut - an index key must be a column of
        /// the table it indexes, so no index on <c>copilot_chats</c> can be date-ordered unless the date is
        /// on <c>copilot_chats</c>. Full option comparison, including why an indexed view was rejected, is
        /// on the migration <c>DenormaliseCopilotChatUserAndTime</c>.
        /// </para>
        /// <para>
        /// Nullable, and deliberately so: a row whose audit event has been removed keeps NULL, and every
        /// query filters on <c>time_stamp &gt;= @from</c>, which excludes NULLs. That is exactly the population
        /// the previous <c>INNER JOIN dbo.audit_events</c> produced, so the semantics are unchanged.
        /// </para>
        /// </summary>
        [Column("time_stamp")]
        public DateTime? TimeStampUtc { get; set; }
    }

    /// <summary>
    /// An event with more data specific to copilot. File/meeting/etc.
    /// Links to common copilot chat event, which links to common audit event.
    /// </summary>
    public abstract class BaseCopilotSpecificEvent
    {
        [Key]
        [ForeignKey(nameof(RelatedChat))]
        [Column("copilot_chat_id")]
        public Guid ChatId { get; set; }

        public CopilotChat RelatedChat { get; set; } = null;

        public abstract string GetEventDescription();
    }


    [Table("copilot_event_files")]
    public class CopilotEventMetadataFile : BaseCopilotSpecificEvent
    {
        [ForeignKey(nameof(FileExtension))]
        [Column("file_extension_id")]
        public int? FileExtensionId { get; set; } = 0;
        public SPEventFileExtension FileExtension { get; set; } = null;

        [ForeignKey(nameof(FileName))]
        [Column("file_name_id")]
        public int? FileNameId { get; set; } = 0;
        public SPEventFileName FileName { get; set; } = null;

        [ForeignKey(nameof(Url))]
        [Column("url_id")]
        public int UrlId { get; set; } = 0;
        public Url Url { get; set; } = null;

        [ForeignKey(nameof(Site))]
        [Column("site_id")]
        public int SiteId { get; set; } = 0;
        public Site Site { get; set; } = null;

        public override string GetEventDescription()
        {
            return $"{FileName?.Name}";
        }
    }

    [Table("copilot_event_meetings")]
    public class CopilotEventMetadataMeeting : BaseCopilotSpecificEvent
    {
        [ForeignKey(nameof(OnlineMeeting))]
        [Column("meeting_id")]
        public int OnlineMeetingId { get; set; }

        public OnlineMeeting OnlineMeeting { get; set; } = null;

        public override string GetEventDescription()
        {
            return $"{OnlineMeeting.Name}";
        }
    }

    [Table("copilot_agents")]
    public class CopilotAgent : AbstractEFEntityWithName
    {
        [Column("agent_id")]
        public string AgentID { get; set; } = null;

        /// <summary>
        /// Indicates whether this is a custom agent (extracted from AppIdentity) or a standard Copilot agent.
        /// Nullable to support backward compatibility with existing data.
        /// </summary>
        [Column("is_custom_agent")]
        public bool? IsCustomAgent { get; set; }
    }

    /// <summary>
    /// Lookup table for accessed resource IDs
    /// </summary>
    [Table("copilot_event_accessed_resource_ids")]
    public class CopilotAccessedResourceId : AbstractEFEntity
    {
        [Column("resource_id")]
        [MaxLength(5000)]
        public string ResourceId { get; set; } = null;
    }

    /// <summary>
    /// Lookup table for accessed resource names
    /// </summary>
    [Table("copilot_event_accessed_resource_names")]
    public class CopilotAccessedResourceName : AbstractEFEntity
    {
        // Not using AbstractEFEntityWithName to allow longer names
        [Column("name")]
        public string Name { get; set; }
    }

    /// <summary>
    /// Lookup table for accessed resource site URLs
    /// </summary>
    [Table("copilot_event_accessed_resource_site_urls")]
    public class CopilotAccessedResourceSiteUrl : AbstractEFEntity
    {
        [Column("site_url")]
        public string SiteUrl { get; set; }
    }

    /// <summary>
    /// Lookup table for accessed resource types
    /// </summary>
    [Table("copilot_event_accessed_resource_types")]
    public class CopilotAccessedResourceType : AbstractEFEntityWithName
    {
    }

    /// <summary>
    /// Lookup table for the action performed against an accessed resource during a Copilot
    /// interaction (schema field <c>AccessedResources[].Action</c>, e.g. "Read").
    /// Dimensioned rather than stored inline because <c>copilot_event_accessed_resources</c> is the
    /// largest Copilot table (rows = interactions x resources) and the value set is tiny.
    /// </summary>
    [Table("copilot_event_accessed_resource_actions")]
    public class CopilotAccessedResourceAction : AbstractEFEntityWithName
    {
    }

    /// <summary>
    /// Lookup table for sensitivity label IDs.
    /// Shared across multiple event types (Copilot, SharePoint, etc.)
    /// </summary>
    [Table("sensitivity_labels")]
    public class SensitivityLabel : AbstractEFEntity
    {
        [Column("label_id")]
        [MaxLength(100)]
        public string LabelId { get; set; } = null;
    }

    /// <summary>
    /// Junction table linking copilot events to accessed resources
    /// </summary>
    [Table("copilot_event_accessed_resources")]
    public class CopilotEventAccessedResource : AbstractEFEntity
    {
        [ForeignKey(nameof(RelatedChat))]
        [Column("copilot_chat_id")]
        public Guid ChatId { get; set; }
        public CopilotChat RelatedChat { get; set; } = null;

        [ForeignKey(nameof(ResourceId))]
        [Column("resource_id_id")]
        public int? ResourceIdId { get; set; }
        public CopilotAccessedResourceId ResourceId { get; set; } = null;

        [ForeignKey(nameof(ResourceName))]
        [Column("resource_name_id")]
        public int? ResourceNameId { get; set; }
        public CopilotAccessedResourceName ResourceName { get; set; } = null;

        [ForeignKey(nameof(ResourceSiteUrl))]
        [Column("resource_site_url_id")]
        public int? ResourceSiteUrlId { get; set; }
        public CopilotAccessedResourceSiteUrl ResourceSiteUrl { get; set; } = null;

        [ForeignKey(nameof(ResourceType))]
        [Column("resource_type_id")]
        public int? ResourceTypeId { get; set; }
        public CopilotAccessedResourceType ResourceType { get; set; } = null;

        [ForeignKey(nameof(SensitivityLabel))]
        [Column("sensitivity_label_id")]
        public int? SensitivityLabelId { get; set; }
        public SensitivityLabel SensitivityLabel { get; set; } = null;

        /// <summary>
        /// The action Copilot performed against the resource (schema field <c>Action</c>, e.g. "Read").
        /// </summary>
        [ForeignKey(nameof(Action))]
        [Column("action_id")]
        public int? ActionId { get; set; }
        public CopilotAccessedResourceAction Action { get; set; } = null;

        /// <summary>
        /// The SharePoint list item unique id backing this resource (schema field
        /// <c>listItemUniqueId</c>). Deliberately dimensioned against the SAME lookup table as
        /// <see cref="ResourceId"/>: the audit payload very often repeats the resource Id verbatim as
        /// listItemUniqueId, and both are opaque resource identifiers from the same value domain, so
        /// sharing the dimension de-duplicates them against each other instead of storing the string
        /// twice on the largest Copilot table.
        /// </summary>
        [ForeignKey(nameof(ListItemUniqueId))]
        [Column("list_item_unique_id_id")]
        public int? ListItemUniqueIdId { get; set; }
        public CopilotAccessedResourceId ListItemUniqueId { get; set; } = null;
    }

    /// <summary>
    /// Lookup table for Copilot interaction context types (schema field <c>Contexts[].Type</c>),
    /// e.g. "docx", "TeamsMeeting", "TeamsChannel", "TeamsChat".
    /// </summary>
    [Table("copilot_event_context_types")]
    public class CopilotContextType : AbstractEFEntityWithName
    {
    }

    /// <summary>
    /// Every context attached to a Copilot interaction (schema collection <c>Contexts</c>) - i.e.
    /// where the user was when they used Copilot.
    ///
    /// The importer's file/meeting resolution deliberately only acts on the FIRST file or meeting
    /// context (see CopilotAuditEventManager), so <c>copilot_event_files</c> /
    /// <c>copilot_event_meetings</c> capture at most one context per interaction and everything else
    /// used to be discarded. This table records ALL of them, unresolved and verbatim, so no context
    /// is silently lost.
    /// </summary>
    [Table("copilot_event_contexts")]
    public class CopilotEventContext : AbstractEFEntity
    {
        [ForeignKey(nameof(RelatedChat))]
        [Column("copilot_chat_id")]
        public Guid ChatId { get; set; }
        public CopilotChat RelatedChat { get; set; } = null;

        /// <summary>
        /// The context identifier (schema field <c>Contexts[].Id</c>) - typically a document URL or a
        /// Teams thread id. Stored inline (rather than dimensioned) because there is roughly one
        /// context per interaction, so a lookup table would add a merge pass and a report join for a
        /// table that is already interaction-sized. nvarchar(850) = the widest Unicode-safe indexable
        /// string, matching the accessed-resource lookup columns.
        /// </summary>
        [Column("context_ref")]
        [MaxLength(850)]
        public string ContextRef { get; set; } = null;

        [ForeignKey(nameof(ContextType))]
        [Column("context_type_id")]
        public int? ContextTypeId { get; set; }
        public CopilotContextType ContextType { get; set; } = null;

        /// <summary>
        /// Identifier of the container the context belongs to, e.g. a Teams team or a SharePoint
        /// container (schema field <c>Contexts[].ContainerId</c>).
        /// </summary>
        [Column("container_id")]
        [MaxLength(450)]
        public string ContainerId { get; set; } = null;
    }

    #region Message Tracking Tables

    /// <summary>
    /// Represents a message in a Copilot conversation - BOTH the user prompt and the Copilot
    /// response (see <see cref="IsPrompt"/>).
    ///
    /// Historically only response messages were imported, because the only field stored was the
    /// message id and prompts added rows without adding information. Now that
    /// <see cref="Size"/> is persisted, the prompt rows carry the input volume of the interaction -
    /// which is only obtainable from the prompt - and <see cref="IsPrompt"/> would be a constant
    /// (always false) if prompts were still dropped. Both are therefore imported. The trade-off is
    /// roughly twice as many rows in this table (payloads normally contain one prompt and one
    /// response per interaction).
    /// </summary>
    [Table("copilot_event_messages")]
    public class CopilotMessage : AbstractEFEntity
    {
        [ForeignKey(nameof(RelatedChat))]
        [Column("copilot_chat_id")]
        public Guid ChatId { get; set; }
        public CopilotChat RelatedChat { get; set; } = null;

        [Column("message_id")]
        [MaxLength(500)]
        public string MessageId { get; set; } = null;

        /// <summary>
        /// Size of the message as reported by the audit schema (<c>Size</c>, Edm.Int64). Null when
        /// the payload omits it (Microsoft does not populate it for every host).
        /// </summary>
        [Column("size")]
        public long? Size { get; set; }

        /// <summary>
        /// True for the user's prompt, false for Copilot's response (schema field <c>isPrompt</c>).
        /// Null when the payload omits it.
        /// </summary>
        [Column("is_prompt")]
        public bool? IsPrompt { get; set; }
    }

    #endregion

    #region AI Model Transparency Tables

    /// <summary>
    /// Lookup table for AI models used in Copilot conversations.
    /// Stores unique model names like "DEEP_LEO" for deep reasoning, together with the provider and
    /// version reported alongside them (<c>ModelTransparencyDetails</c>).
    ///
    /// The de-duplication key is the whole (name, provider, version) tuple, not just the name: the
    /// model version is part of a model's identity for AI-transparency reporting, and a tuple key
    /// keeps history additive (no update pass over the dimension). Reports that only care about the
    /// model itself can still group by <c>name</c>. Rows imported before this change keep NULL
    /// provider/version and are reused for payloads that omit them.
    /// </summary>
    [Table("copilot_ai_models")]
    public class CopilotAIModel : AbstractEFEntityWithName
    {
        // Name inherited from AbstractEFEntityWithName

        /// <summary>
        /// The model provider (schema field <c>ModelProviderName</c>), e.g. "OpenAI".
        /// </summary>
        [Column("provider_name")]
        [MaxLength(100)]
        public string ProviderName { get; set; } = null;

        /// <summary>
        /// The model version (schema field <c>ModelVersion</c>).
        /// </summary>
        [Column("version")]
        [MaxLength(100)]
        public string Version { get; set; } = null;
    }

    /// <summary>
    /// Junction table linking Copilot events to the AI models used.
    /// Tracks which AI models were involved in generating responses for each conversation.
    /// </summary>
    [Table("copilot_event_ai_models")]
    public class CopilotEventAIModel : AbstractEFEntity
    {
        [ForeignKey(nameof(RelatedChat))]
        [Column("copilot_chat_id")]
        public Guid ChatId { get; set; }
        public CopilotChat RelatedChat { get; set; } = null;

        [ForeignKey(nameof(AIModel))]
        [Column("model_id")]
        public int ModelId { get; set; }
        public CopilotAIModel AIModel { get; set; } = null;
    }

    #endregion

    #region AI System Plugin Tables

    /// <summary>
    /// Lookup table for the AI system plugins / connectors that can ground a Copilot answer
    /// (schema collection <c>AISystemPlugin</c>, e.g. Id "BingWebSearch" / Name "BuiltIn").
    /// Mirrors <see cref="CopilotAIModel"/>: de-duplicated on the whole (plugin id, name, version)
    /// tuple so a plugin upgrade shows as a new row rather than silently rewriting history.
    /// </summary>
    [Table("copilot_ai_system_plugins")]
    public class CopilotAISystemPlugin : AbstractEFEntity
    {
        /// <summary>
        /// The plugin's own identifier from the audit payload (schema field <c>Id</c>).
        /// Named like <see cref="CopilotAgent.AgentID"/>: <c>id</c> is the surrogate key, this is the
        /// external one.
        /// </summary>
        [Column("plugin_id")]
        [MaxLength(255)]
        public string PluginId { get; set; } = null;

        [Column("name")]
        [MaxLength(255)]
        public string Name { get; set; } = null;

        [Column("version")]
        [MaxLength(50)]
        public string Version { get; set; } = null;
    }

    /// <summary>
    /// Junction table linking Copilot events to the AI system plugins invoked during them.
    /// Shows which plugins/connectors grounded each answer.
    /// </summary>
    [Table("copilot_event_ai_system_plugins")]
    public class CopilotEventAISystemPlugin : AbstractEFEntity
    {
        [ForeignKey(nameof(RelatedChat))]
        [Column("copilot_chat_id")]
        public Guid ChatId { get; set; }
        public CopilotChat RelatedChat { get; set; } = null;

        [ForeignKey(nameof(AISystemPlugin))]
        [Column("ai_system_plugin_id")]
        public int AISystemPluginId { get; set; }
        public CopilotAISystemPlugin AISystemPlugin { get; set; } = null;
    }

    #endregion

}

