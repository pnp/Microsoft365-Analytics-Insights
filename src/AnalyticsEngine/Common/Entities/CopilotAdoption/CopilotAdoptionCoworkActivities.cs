using System;
using System.Collections.Generic;
using System.Linq;

namespace Common.Entities.CopilotAdoption
{
    /// <summary>
    /// The kinds of work the Cowork estimate models for the people not yet running Cowork tasks: each
    /// thing Microsoft says Cowork does, matched to the count Microsoft's usage reports already keep of
    /// people doing it by hand.
    /// </summary>
    /// <remarks>
    /// <para><b>Why the estimate is broken down this way.</b> It used to project a flat number of Cowork
    /// tasks onto every person - the tenant's Cowork users' average, or a placeholder of twenty - which
    /// said nothing about <i>where</i> Cowork would save time and modelled someone who organises ten
    /// meetings a week exactly like someone who organises none. The usage reports already record what
    /// each person does without Cowork, so the estimate now starts from that: for each kind of work, the
    /// volume a person already handles, the share of it they are assumed to hand to Cowork, and the
    /// minutes Cowork is assumed to save on each piece. The volumes are observed; the share and the
    /// minutes are assumptions the reader can replace, one pair per kind of work.</para>
    ///
    /// <para><b>Microsoft's own list, and only what the reports can count.</b> Microsoft describes
    /// Cowork as sending emails, scheduling meetings, creating documents, posting in Teams and managing
    /// the calendar, and preparing briefings. Each entry here is one of those, against the signal that
    /// counts the same work done by hand. What the reports cannot count - research, searching, scheduled
    /// automations - is left out, so the estimate understates rather than invents. Calendar responses
    /// (Outlook's <c>meeting_interacted_count</c>) and meeting invitations sent
    /// (<c>meeting_created_count</c>) would be better signals for calendar work, but neither is in the
    /// columnstore index <c>ColumnstoreUsageReportMetrics</c> built for these queries, and reading either
    /// would take the Cowork readiness query off that index for <c>outlook_user_activity_log</c>. Every
    /// column used here is already in the index.</para>
    ///
    /// <para><b>Order is part of the contract.</b> The server publishes the volumes in this order and
    /// sums the minutes in this order, and the portal's <c>COWORK_ACTIVITIES</c> uses the same order, so
    /// the two produce the same floating-point total and agree to the hour.</para>
    /// </remarks>
    public static class CoworkActivities
    {
        public const string OrganiseMeetings = "organiseMeetings";
        public const string PrepareMeetings = "prepareMeetings";
        public const string SendEmail = "sendEmail";
        public const string PostInTeams = "postInTeams";
        public const string CreateDocuments = "createDocuments";

        /// <summary>Every kind of work, in publication order.</summary>
        public static IReadOnlyList<CoworkActivity> All { get; } = new[]
        {
            new CoworkActivity(
                OrganiseMeetings,
                "Organise meetings",
                "Meetings organised in Teams",
                "One in four. Arranging a meeting is the classic delegated task, but Teams counts every meeting "
                + "a person organised - each occurrence of a recurring series, and ad hoc calls that needed no "
                + "arranging - so only a minority are work to hand over.",
                "coworkOrganiseMeetingsShare",
                "coworkOrganiseMeetingsMinutes",
                r => r.MeetingsOrganisedPerActiveDay,
                o => o.CoworkOrganiseMeetingsShare,
                (o, v) => o.CoworkOrganiseMeetingsShare = v,
                o => o.CoworkOrganiseMeetingsMinutes,
                (o, v) => o.CoworkOrganiseMeetingsMinutes = v),
            new CoworkActivity(
                PrepareMeetings,
                "Prepare for meetings",
                "Meetings attended in Teams",
                "One in ten. A briefing is worth having for the meetings that need preparing for, not for every "
                + "stand-up, and Microsoft 365 Copilot already prepares one when asked - Cowork's part is doing "
                + "it unasked, as part of a daily briefing.",
                "coworkPrepareMeetingsShare",
                "coworkPrepareMeetingsMinutes",
                r => r.MeetingsAttendedPerActiveDay,
                o => o.CoworkPrepareMeetingsShare,
                (o, v) => o.CoworkPrepareMeetingsShare = v,
                o => o.CoworkPrepareMeetingsMinutes,
                (o, v) => o.CoworkPrepareMeetingsMinutes = v),
            new CoworkActivity(
                SendEmail,
                "Send email",
                "Emails sent in Outlook",
                "One in twenty. Most emails are quick replies, faster to type than to describe to an agent; the "
                + "ones worth handing over are status updates, follow-ups and messages to stakeholders.",
                "coworkSendEmailShare",
                "coworkSendEmailMinutes",
                r => r.EmailsSentPerActiveDay,
                o => o.CoworkSendEmailShare,
                (o, v) => o.CoworkSendEmailShare = v,
                o => o.CoworkSendEmailMinutes,
                (o, v) => o.CoworkSendEmailMinutes = v),
            new CoworkActivity(
                PostInTeams,
                "Post in Teams",
                "Teams chat and channel messages",
                "One in a hundred. Almost every Teams message is part of a conversation; the ones worth handing "
                + "over are updates and announcements.",
                "coworkPostInTeamsShare",
                "coworkPostInTeamsMinutes",
                r => r.ChatAndChannelMessagesPerActiveDay,
                o => o.CoworkPostInTeamsShare,
                (o, v) => o.CoworkPostInTeamsShare = v,
                o => o.CoworkPostInTeamsMinutes,
                (o, v) => o.CoworkPostInTeamsMinutes = v),
            new CoworkActivity(
                CreateDocuments,
                "Create documents",
                "Files viewed or edited in SharePoint and OneDrive",
                "One in fifty. Microsoft's reports count every file opened as well as every file edited, so most "
                + "of this is reading; a document Cowork could build from scratch is the exception.",
                "coworkCreateDocumentsShare",
                "coworkCreateDocumentsMinutes",
                r => r.FilesPerActiveDay,
                o => o.CoworkCreateDocumentsShare,
                (o, v) => o.CoworkCreateDocumentsShare = v,
                o => o.CoworkCreateDocumentsMinutes,
                (o, v) => o.CoworkCreateDocumentsMinutes = v),
        };

        /// <summary>The kind of work with this key, or null for a key this build does not know.</summary>
        public static CoworkActivity Find(string key)
        {
            return All.FirstOrDefault(a => string.Equals(a.Key, key, StringComparison.Ordinal));
        }
    }

    /// <summary>One kind of work Cowork could take on: what counts it, and the two assumptions applied to it.</summary>
    public sealed class CoworkActivity
    {
        private readonly Func<CoworkReadinessRow, double> _perActiveDay;
        private readonly Func<CopilotAdoptionOptions, double> _share;
        private readonly Action<CopilotAdoptionOptions, double> _setShare;
        private readonly Func<CopilotAdoptionOptions, double> _minutes;
        private readonly Action<CopilotAdoptionOptions, double> _setMinutes;

        internal CoworkActivity(
            string key,
            string label,
            string volumeLabel,
            string shareRationale,
            string shareOption,
            string minutesOption,
            Func<CoworkReadinessRow, double> perActiveDay,
            Func<CopilotAdoptionOptions, double> share,
            Action<CopilotAdoptionOptions, double> setShare,
            Func<CopilotAdoptionOptions, double> minutes,
            Action<CopilotAdoptionOptions, double> setMinutes)
        {
            Key = key;
            Label = label;
            VolumeLabel = volumeLabel;
            ShareRationale = shareRationale;
            ShareOption = shareOption;
            MinutesOption = minutesOption;
            _perActiveDay = perActiveDay;
            _share = share;
            _setShare = setShare;
            _minutes = minutes;
            _setMinutes = setMinutes;
        }

        /// <summary>The stable key the API and the portal use, e.g. <c>organiseMeetings</c>.</summary>
        public string Key { get; }

        /// <summary>What Cowork does, in English, for the workbook - "Organise meetings".</summary>
        public string Label { get; }

        /// <summary>What the observed volume counts, in English - "Meetings organised in Teams".</summary>
        public string VolumeLabel { get; }

        /// <summary>Why the default share is what it is, in English, for the workbook.</summary>
        public string ShareRationale { get; }

        /// <summary>The serialised name of the share option - also its export query parameter.</summary>
        public string ShareOption { get; }

        /// <summary>The serialised name of the minutes option - also its export query parameter.</summary>
        public string MinutesOption { get; }

        /// <summary>This row's volume of the work on a typical active day. Never negative.</summary>
        public double PerActiveDay(CoworkReadinessRow row)
        {
            return row == null ? 0d : Finite(_perActiveDay(row));
        }

        /// <summary>
        /// The share of this work handed to Cowork, clamped to 0..1: above 1 the model would hand Cowork
        /// more work than the person does.
        /// </summary>
        public double Share(CopilotAdoptionOptions options)
        {
            return Math.Min(1d, Finite(_share(options ?? CopilotAdoptionOptions.Default)));
        }

        /// <summary>The minutes Cowork saves on each piece of this work it takes on. Never negative.</summary>
        public double Minutes(CopilotAdoptionOptions options)
        {
            return Finite(_minutes(options ?? CopilotAdoptionOptions.Default));
        }

        internal void SetShare(CopilotAdoptionOptions options, double value)
        {
            _setShare(options, value);
        }

        internal void SetMinutes(CopilotAdoptionOptions options, double value)
        {
            _setMinutes(options, value);
        }

        private static double Finite(double value)
        {
            return double.IsNaN(value) || double.IsInfinity(value) ? 0d : Math.Max(0d, value);
        }
    }
}
