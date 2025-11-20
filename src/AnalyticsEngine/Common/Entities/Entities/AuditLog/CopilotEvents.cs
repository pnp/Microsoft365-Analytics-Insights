using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities.Entities.AuditLog
{

    /// <summary>
    /// A copilot interaction event. May not be related to any file or meeting. 
    /// Relates back to a common audit event.
    /// </summary>
    [Table("event_copilot_chats")]
    public class CopilotChat : BaseOfficeEvent
    {
        [Column("app_host")]
        public string AppHost { get; set; } = null;


        [ForeignKey(nameof(Agent))]
        [Column("agent_id")]
        public int? AgentId { get; set; }
        public CopilotAgent Agent { get; set; } = null;

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


    [Table("event_copilot_files")]
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

    [Table("event_copilot_meetings")]
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
    /// Lookup table for accessed resource types
    /// </summary>
    [Table("copilot_event_accessed_resource_types")]
    public class CopilotAccessedResourceType : AbstractEFEntityWithName
    {
    }

    /// <summary>
    /// Lookup table for sensitivity label IDs
    /// </summary>
    [Table("copilot_event_sensitivity_labels")]
    public class CopilotSensitivityLabel : AbstractEFEntity
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

        [ForeignKey(nameof(ResourceType))]
        [Column("resource_type_id")]
        public int? ResourceTypeId { get; set; }
        public CopilotAccessedResourceType ResourceType { get; set; } = null;

        [ForeignKey(nameof(SensitivityLabel))]
        [Column("sensitivity_label_id")]
        public int? SensitivityLabelId { get; set; }
        public CopilotSensitivityLabel SensitivityLabel { get; set; } = null;
    }

    #region Message Tracking Tables

    /// <summary>
    /// Represents a message in a Copilot conversation.
    /// Messages can be prompts (user input) or responses (Copilot output).
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

        [Column("is_prompt")]
        public bool IsPrompt { get; set; }

        /// <summary>
        /// Type of response: Classic, Generative, or TenantGraph
        /// </summary>
        [ForeignKey(nameof(MessageType))]
        [Column("message_type_id")]
        public int? MessageTypeId { get; set; }
        public CopilotMessageType MessageType { get; set; } = null;
    }

    /// <summary>
    /// Lookup table for message types (Classic, Generative, TenantGraph)
    /// </summary>
    [Table("copilot_event_message_types")]
    public class CopilotMessageType : AbstractEFEntityWithName
    {
    }

    #endregion

    #region Agent Action Tracking Tables

    /// <summary>
    /// Represents an agent action such as triggers, deep reasoning, topic transitions, etc.
    /// </summary>
    [Table("copilot_event_agent_actions")]
    public class CopilotAgentAction : AbstractEFEntity
    {
        [ForeignKey(nameof(RelatedChat))]
        [Column("copilot_chat_id")]
        public Guid ChatId { get; set; }
        public CopilotChat RelatedChat { get; set; } = null;

        [Column("action_id")]
        [MaxLength(500)]
        public string ActionId { get; set; } = null;

        [ForeignKey(nameof(ActionType))]
        [Column("action_type_id")]
        public int? ActionTypeId { get; set; }
        public CopilotAgentActionType ActionType { get; set; } = null;
    }

    /// <summary>
    /// Lookup table for agent action types (Trigger, DeepReasoning, TopicTransition, etc.)
    /// </summary>
    [Table("copilot_event_agent_action_types")]
    public class CopilotAgentActionType : AbstractEFEntityWithName
    {
    }

    #endregion

    #region AI Tool Usage Tracking Tables

    /// <summary>
    /// Represents AI tool usage with tiered billing
    /// </summary>
    [Table("copilot_event_ai_tool_usages")]
    public class CopilotAIToolUsage : AbstractEFEntity
    {
        [ForeignKey(nameof(RelatedChat))]
        [Column("copilot_chat_id")]
        public Guid ChatId { get; set; }
        public CopilotChat RelatedChat { get; set; } = null;

        [Column("tool_id")]
        [MaxLength(500)]
        public string ToolId { get; set; } = null;

        [ForeignKey(nameof(Tier))]
        [Column("tier_id")]
        public int? TierId { get; set; }
        public CopilotAIToolTier Tier { get; set; } = null;

        [Column("response_count")]
        public int ResponseCount { get; set; }
    }

    /// <summary>
    /// Lookup table for AI tool tiers (Basic, Standard, Premium)
    /// </summary>
    [Table("copilot_event_ai_tool_tiers")]
    public class CopilotAIToolTier : AbstractEFEntityWithName
    {
    }

    #endregion

    #region Flow Action Tracking Tables

    /// <summary>
    /// Represents agent flow actions (predefined sequences)
    /// </summary>
    [Table("copilot_event_flow_actions")]
    public class CopilotFlowAction : AbstractEFEntity
    {
        [ForeignKey(nameof(RelatedChat))]
        [Column("copilot_chat_id")]
        public Guid ChatId { get; set; }
        public CopilotChat RelatedChat { get; set; } = null;

        [Column("action_count")]
        public int ActionCount { get; set; }
    }

    #endregion

}

