using Common.Entities.UserFilters;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace Web.AnalyticsWeb.Models.UserFilters
{
    /// <summary>
    /// <c>GET api/GlobalFilter/effective</c>: the administrator's global filter as it applies to the
    /// signed-in person, for the filter bar every insights page shows.
    /// </summary>
    public sealed class GlobalFilterEffectiveModel
    {
        /// <summary>True when a global filter is defined.</summary>
        [JsonProperty("active")]
        public bool Active { get; set; }

        /// <summary>True when it narrows this person's reports - false when none is defined or they switched it off.</summary>
        [JsonProperty("applied")]
        public bool Applied { get; set; }

        /// <summary>True when an administrator switched it off for their own view.</summary>
        [JsonProperty("bypassed")]
        public bool Bypassed { get; set; }

        /// <summary>True when this person may switch it off and edit it: both Administration and See PII are required.</summary>
        [JsonProperty("canBypass")]
        public bool CanBypass { get; set; }

        [JsonProperty("revision")]
        public int Revision { get; set; }

        /// <summary>The conditions with this person's own values filled in. <c>null</c> when none is defined or it cannot be read.</summary>
        [JsonProperty("filter")]
        public GlobalFilterEcho Filter { get; set; }

        /// <summary>True when the stored filter cannot be read by this version: reports are refused until an administrator fixes it.</summary>
        [JsonProperty("invalid")]
        public bool Invalid { get; set; }

        /// <summary>
        /// True when the filter leaves this reader, who lacks See PII, fewer than <see cref="MinimumPeople"/>
        /// people - so their reports are refused with the See PII permission message.
        /// </summary>
        [JsonProperty("tooFewPeople")]
        public bool TooFewPeople { get; set; }

        /// <summary>The fewest people the filter may leave a reader without See PII, other than none.</summary>
        [JsonProperty("minimumPeople")]
        public int MinimumPeople { get; set; } = ReportScopeResolver.MinimumPeopleWithoutSeePii;

        internal static GlobalFilterEffectiveModel From(GlobalFilterApplication global, bool canBypass)
        {
            if (global == null) return new GlobalFilterEffectiveModel { CanBypass = canBypass };

            return new GlobalFilterEffectiveModel
            {
                Active = true,
                Applied = global.Applied,
                Bypassed = global.Bypassed,
                CanBypass = global.CanBypass,
                Revision = global.Revision,
                Filter = global.Echo,
                Invalid = global.Resolved == null,
                TooFewPeople = global.TooFewPeople,
            };
        }
    }

    /// <summary><c>GET api/GlobalFilter</c>: the definition, for the administrator's editor.</summary>
    public sealed class GlobalFilterAdminModel
    {
        /// <summary>The definition in its wire form - what the editor sends back to save. Empty for no filter.</summary>
        [JsonProperty("filter")]
        public string Filter { get; set; } = string.Empty;

        /// <summary>The definition's conditions, without anyone's values filled in.</summary>
        [JsonProperty("clauses")]
        public List<GlobalFilterClauseModel> Clauses { get; set; } = new List<GlobalFilterClauseModel>();

        /// <summary>Send back with a save, so a save from a page opened before someone else's change is refused.</summary>
        [JsonProperty("revision")]
        public int Revision { get; set; }

        [JsonProperty("modifiedUtc")]
        public DateTime? ModifiedUtc { get; set; }

        /// <summary>The sign-in name of the administrator who last saved it.</summary>
        [JsonProperty("modifiedBy")]
        public string ModifiedBy { get; set; }

        /// <summary>False when the database has not been upgraded to hold a global filter, so none can be saved.</summary>
        [JsonProperty("storageAvailable")]
        public bool StorageAvailable { get; set; }

        /// <summary>
        /// False when <c>EnforcePortalRoles=false</c>: everyone who can sign in is an administrator, and can
        /// change the filter or switch it off for themselves.
        /// </summary>
        [JsonProperty("rolesEnforced")]
        public bool RolesEnforced { get; set; }

        /// <summary>True when the stored filter cannot be read by this version - reports are refused until it is replaced.</summary>
        [JsonProperty("invalid")]
        public bool Invalid { get; set; }

        /// <summary>
        /// The fewest people the filter may leave a reader without See PII, other than none: below it, that
        /// reader's reports are refused. Sent so the editor can say so without keeping its own copy of the number.
        /// </summary>
        [JsonProperty("minimumPeopleWithoutSeePii")]
        public int MinimumPeopleWithoutSeePii { get; set; } = ReportScopeResolver.MinimumPeopleWithoutSeePii;
    }

    /// <summary>The body of <c>POST api/GlobalFilter</c>.</summary>
    public sealed class GlobalFilterSaveRequest
    {
        /// <summary>The new definition in its wire form. Empty removes the global filter.</summary>
        [JsonProperty("filter")]
        public string Filter { get; set; }

        /// <summary>The revision the editor opened the filter at.</summary>
        [JsonProperty("revision")]
        public int Revision { get; set; }
    }

    /// <summary>The body of <c>POST api/GlobalFilter/preview</c>.</summary>
    public sealed class GlobalFilterPreviewRequest
    {
        [JsonProperty("filter")]
        public string Filter { get; set; }
    }
}
