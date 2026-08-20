using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities.Entities.Copilot
{
    /// <summary>
    /// One row per Copilot conversation thread seen in the interaction-history feed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>No prompt or response text is ever persisted by this feature.</b> The Graph payload behind these
    /// entities contains the user's literal prompt and Copilot's literal answer - the most sensitive data the
    /// product can see - so the importer reads the body, derives statistics from it (length, word count, and
    /// optionally a sentiment score / detected language / key phrases) and then discards it. Nothing in this
    /// file has a column capable of holding a message body.
    /// </para>
    /// <para>
    /// What this adds over the two Copilot sources we already have: the Audit.General feed
    /// (<c>copilot_chats</c>) gives accessed resources and opaque message sizes, and the Graph Copilot usage
    /// reports give per-user prompt counts aggregated over 7/28/90/180 days. Neither gives turn-level
    /// structure. <see cref="CopilotInteraction.RequestId"/> pairs a user prompt with the Copilot response it
    /// produced, which is what makes response latency, true turn counts and prompt-to-response ratios
    /// possible. <see cref="SessionRef"/> is the same identifier the audit feed records as
    /// <c>copilot_chats.thread_id</c>, so interaction shape can be joined back to the audit-derived accessed
    /// resources for the same conversation.
    /// </para>
    /// </remarks>
    [Table("copilot_interaction_sessions")]
    public class CopilotInteractionSession : AbstractEFEntity
    {
        /// <summary>
        /// Graph <c>sessionId</c> - the thread/conversation identifier, and the join key back to
        /// <c>copilot_chats.thread_id</c>. Capped at 450 characters because it is indexed: the SQL Server
        /// index-key limit is 1700 bytes and nvarchar costs 2 bytes per character.
        /// </summary>
        [Column("session_ref")]
        [MaxLength(450)]
        [Required]
        public string SessionRef { get; set; }

        [ForeignKey(nameof(User))]
        [Column("user_id")]
        public int UserId { get; set; }
        public User User { get; set; } = null;
    }

    /// <summary>
    /// Lookup for Graph <c>appClass</c>, e.g. <c>IPM.SkypeTeams.Message.Copilot.Excel</c> or
    /// <c>IPM.SkypeTeams.Message.Copilot.BizChat</c>. Finer-grained than the audit feed's <c>app_host</c>.
    /// </summary>
    [Table("copilot_interaction_app_classes")]
    public class CopilotInteractionAppClass : AbstractEFEntityWithName
    {
    }

    /// <summary>
    /// Lookup for Graph <c>conversationType</c>, e.g. <c>appchat</c> (Copilot inside a host app) or
    /// <c>bizchat</c> (standalone Microsoft 365 Copilot Chat). Not available from any other source.
    /// </summary>
    [Table("copilot_interaction_conversation_types")]
    public class CopilotInteractionConversationType : AbstractEFEntityWithName
    {
    }

    /// <summary>
    /// Lookup for Graph <c>interactionType</c>: <c>userPrompt</c> or <c>aiResponse</c>.
    /// </summary>
    [Table("copilot_interaction_types")]
    public class CopilotInteractionTypeLookup : AbstractEFEntityWithName
    {
    }

    /// <summary>
    /// Lookup for the sender's Graph <c>locale</c>, e.g. <c>en-us</c>. This is the client locale, which is not
    /// the same thing as the language the prompt was written in - that comes from cognitive language detection
    /// and is stored on <see cref="CopilotInteraction.LanguageId"/>.
    /// </summary>
    [Table("copilot_interaction_locales")]
    public class CopilotInteractionLocale : AbstractEFEntityWithName
    {
    }

    /// <summary>
    /// Lookup for the device the interaction came from (Graph <c>from.device</c>), e.g. <c>desktop</c>,
    /// <c>web</c> or <c>mobile</c>.
    /// </summary>
    [Table("copilot_interaction_devices")]
    public class CopilotInteractionDevice : AbstractEFEntityWithName
    {
    }

    /// <summary>
    /// One row per Copilot interaction - either one user prompt or one Copilot response. Statistics only;
    /// see the remarks on <see cref="CopilotInteractionSession"/> for why no body text is stored.
    /// </summary>
    [Table("copilot_interactions")]
    public class CopilotInteraction : AbstractEFEntity
    {
        /// <summary>
        /// Graph <c>id</c> for the interaction. Only guaranteed unique within a session, hence the unique
        /// index on (<c>session_id</c>, <c>graph_interaction_id</c>) rather than on this column alone.
        /// </summary>
        [Column("graph_interaction_id")]
        [MaxLength(200)]
        [Required]
        public string GraphInteractionId { get; set; }

        [ForeignKey(nameof(Session))]
        [Column("session_id")]
        public int SessionId { get; set; }
        public CopilotInteractionSession Session { get; set; } = null;

        /// <summary>
        /// Denormalised from the session so per-user reporting doesn't need a join. The importer always sets
        /// this to the same user as the parent session.
        /// </summary>
        [ForeignKey(nameof(User))]
        [Column("user_id")]
        public int UserId { get; set; }
        public User User { get; set; } = null;

        /// <summary>
        /// Graph <c>requestId</c>: groups a user prompt with the Copilot response it produced. This is the
        /// most valuable new field in the feed - it is what turns a flat list of messages into turns, and it
        /// is how <see cref="ResponseLatencyMs"/> is calculated.
        /// </summary>
        [Column("request_id")]
        [MaxLength(200)]
        public string RequestId { get; set; }

        [ForeignKey(nameof(InteractionType))]
        [Column("interaction_type_id")]
        public int? InteractionTypeId { get; set; }
        public CopilotInteractionTypeLookup InteractionType { get; set; } = null;

        [ForeignKey(nameof(AppClass))]
        [Column("app_class_id")]
        public int? AppClassId { get; set; }
        public CopilotInteractionAppClass AppClass { get; set; } = null;

        [ForeignKey(nameof(ConversationType))]
        [Column("conversation_type_id")]
        public int? ConversationTypeId { get; set; }
        public CopilotInteractionConversationType ConversationType { get; set; } = null;

        [ForeignKey(nameof(Locale))]
        [Column("locale_id")]
        public int? LocaleId { get; set; }
        public CopilotInteractionLocale Locale { get; set; } = null;

        [ForeignKey(nameof(Device))]
        [Column("device_id")]
        public int? DeviceId { get; set; }
        public CopilotInteractionDevice Device { get; set; } = null;

        /// <summary>Graph <c>createdDateTime</c>, stored as UTC. Also drives the per-user import watermark.</summary>
        [Column("created_utc")]
        public DateTime CreatedUtc { get; set; }

        /// <summary>
        /// Characters in the interaction body after HTML has been stripped. A size signal only - the text
        /// itself is discarded once it has been counted.
        /// </summary>
        [Column("body_char_count")]
        public int BodyCharCount { get; set; }

        /// <summary>Whitespace-delimited word count of the stripped body.</summary>
        [Column("body_word_count")]
        public int BodyWordCount { get; set; }

        [Column("attachment_count")]
        public int AttachmentCount { get; set; }

        [Column("link_count")]
        public int LinkCount { get; set; }

        [Column("mention_count")]
        public int MentionCount { get; set; }

        [Column("context_count")]
        public int ContextCount { get; set; }

        /// <summary>
        /// Milliseconds between the user prompt and the Copilot response sharing this
        /// <see cref="RequestId"/>. Only ever set on <c>aiResponse</c> rows, and only when the matching prompt
        /// arrived in the same import batch. Null on prompts, and on responses whose prompt wasn't seen.
        /// </summary>
        [Column("response_latency_ms")]
        public int? ResponseLatencyMs { get; set; }

        /// <summary>
        /// Positive-sentiment confidence (0.0-1.0) from Azure AI Language. Only populated for user prompts,
        /// and only when cognitive services are configured.
        /// </summary>
        [Column("sentiment_score")]
        public double? SentimentScore { get; set; }

        /// <summary>
        /// Language detected in the prompt by Azure AI Language. Only populated for user prompts when
        /// cognitive services are configured. Reuses the shared <c>languages</c> lookup.
        /// </summary>
        [ForeignKey(nameof(Language))]
        [Column("language_id")]
        public int? LanguageId { get; set; }
        public Language Language { get; set; } = null;
    }

    /// <summary>
    /// Key phrases extracted from a user prompt by Azure AI Language, linked to the shared <c>keywords</c>
    /// lookup that Teams channel analysis already uses.
    /// </summary>
    /// <remarks>
    /// These are short topical phrases ("quarterly sales forecast"), not the prompt itself. They are only
    /// produced when cognitive services are enabled, and only for <c>userPrompt</c> interactions.
    /// </remarks>
    [Table("copilot_interaction_keywords")]
    public class CopilotInteractionKeyword : AbstractEFEntity
    {
        [ForeignKey(nameof(Interaction))]
        [Column("interaction_id")]
        public int InteractionId { get; set; }
        public CopilotInteraction Interaction { get; set; } = null;

        [ForeignKey(nameof(KeyWord))]
        [Column("keyword_id")]
        public int KeyWordId { get; set; }
        public KeyWord KeyWord { get; set; } = null;
    }

    /// <summary>
    /// Per-user import state. This is what makes the import incremental and resumable, which matters far more
    /// here than for any other import: the Graph API is one HTTP call per user, so a tenant at the
    /// ~200k-user design target could never be re-scanned from scratch on every cycle.
    /// </summary>
    [Table("copilot_interaction_user_watermarks")]
    public class CopilotInteractionUserWatermark : AbstractEFEntity
    {
        [ForeignKey(nameof(User))]
        [Column("user_id")]
        public int UserId { get; set; }
        public User User { get; set; } = null;

        /// <summary>
        /// <c>createdDateTime</c> of the newest interaction successfully imported for this user. The next run
        /// asks Graph only for interactions after this instant.
        /// </summary>
        [Column("last_interaction_utc")]
        public DateTime? LastInteractionUtc { get; set; }

        /// <summary>When this user was last attempted, whether or not anything came back.</summary>
        [Column("last_run_utc")]
        public DateTime? LastRunUtc { get; set; }

        /// <summary>
        /// Consecutive attempts that returned nothing or failed, used to back off users who will never return
        /// data - most commonly because they have no <c>M365_COPILOT_BUSINESS_CHAT</c> service plan. Reset to
        /// zero by any run that returns interactions.
        /// </summary>
        [Column("consecutive_empty_or_failed")]
        public int ConsecutiveEmptyOrFailed { get; set; }

        /// <summary>
        /// Don't call Graph for this user again until this time. Set once a user has repeatedly returned
        /// nothing, so an unlicensed majority can't burn the cycle's call budget.
        /// </summary>
        [Column("skip_until_utc")]
        public DateTime? SkipUntilUtc { get; set; }

        [Column("last_error")]
        [MaxLength(500)]
        public string LastError { get; set; }
    }

    /// <summary>
    /// One row per import run, for diagnostics - the same pattern the Copilot usage-report import uses.
    /// Because this import costs one Graph call per in-scope user, an admin needs to see how much of the
    /// cycle it consumed and how many of those calls were actually productive.
    /// </summary>
    [Table("copilot_interaction_import_log")]
    public class CopilotInteractionImportLog : AbstractEFEntity
    {
        [Column("run_started_utc")]
        public DateTime RunStartedUtc { get; set; }

        [Column("run_finished_utc")]
        public DateTime? RunFinishedUtc { get; set; }

        /// <summary>Users matching the configured group scope, before the per-cycle cap was applied.</summary>
        [Column("users_in_scope")]
        public int UsersInScope { get; set; }

        /// <summary>Users actually called this run, after the cap and the back-off skip list.</summary>
        [Column("users_scanned")]
        public int UsersScanned { get; set; }

        /// <summary>Users skipped because they were still inside a back-off window.</summary>
        [Column("users_skipped")]
        public int UsersSkipped { get; set; }

        [Column("users_failed")]
        public int UsersFailed { get; set; }

        [Column("interactions_read")]
        public int InteractionsRead { get; set; }

        [Column("interactions_saved")]
        public int InteractionsSaved { get; set; }

        /// <summary>Prompts sent to Azure AI Language, so the cognitive spend is auditable.</summary>
        [Column("cognitive_docs_scored")]
        public int CognitiveDocsScored { get; set; }

        [Column("error")]
        [MaxLength(1000)]
        public string Error { get; set; }
    }
}
