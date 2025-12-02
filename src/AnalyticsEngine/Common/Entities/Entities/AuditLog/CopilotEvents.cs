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
    }

    #region Message Tracking Tables

    /// <summary>
    /// Represents a Copilot response message in a conversation.
    /// Note: Only response messages (not user prompts) are tracked in the import process.
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
    }

    #endregion

    #region AI Model Transparency Tables

    /// <summary>
    /// Lookup table for AI model names used in Copilot conversations.
    /// Stores unique model names like "DEEP_LEO" for deep reasoning.
    /// </summary>
    [Table("copilot_ai_models")]
    public class CopilotAIModel : AbstractEFEntityWithName
    {
        // Name inherited from AbstractEFEntityWithName
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

}

