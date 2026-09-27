using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Common.Entities.UserFilters
{
    /// <summary>One attribute a filter can use, as the portal's property picker needs it.</summary>
    public sealed class UserFilterDimensionModel
    {
        /// <summary>A key from <see cref="UserFilterDimensions"/>.</summary>
        [JsonProperty("key")]
        public string Key { get; set; }

        /// <summary><c>entra</c> for a standard Entra ID attribute, <c>custom</c> for an admin-defined organisation type.</summary>
        [JsonProperty("kind")]
        public string Kind { get; set; }

        /// <summary>The admin's name for a custom organisation type. <c>null</c> for an Entra attribute, which the portal labels itself.</summary>
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("orgTypeId")]
        public int? OrgTypeId { get; set; }

        [JsonProperty("distinctValues")]
        public int DistinctValues { get; set; }

        [JsonProperty("peopleWithValue")]
        public int PeopleWithValue { get; set; }

        /// <summary>Whether "contains" and "does not contain" are offered.</summary>
        [JsonProperty("supportsTextMatch")]
        public bool SupportsTextMatch { get; set; }

        /// <summary>
        /// True when the values are product tokens (<see cref="UserFilterTokens"/>) the portal translates,
        /// rather than tenant data it must show verbatim.
        /// </summary>
        [JsonProperty("fixedValues")]
        public bool FixedValues { get; set; }
    }

    public sealed class UserFilterDimensionList
    {
        /// <summary>How many people the directory holds.</summary>
        [JsonProperty("people")]
        public int People { get; set; }

        /// <summary>When the directory was read - so the page can say how current the values are.</summary>
        [JsonProperty("loadedUtc")]
        public DateTime LoadedUtc { get; set; }

        [JsonProperty("dimensions")]
        public List<UserFilterDimensionModel> Dimensions { get; set; } = new List<UserFilterDimensionModel>();
    }

    public sealed class UserFilterValueModel
    {
        [JsonProperty("value")]
        public string Value { get; set; }

        /// <summary>
        /// How many people hold the value - or, for the management chain, how many report to the manager
        /// at any level.
        /// </summary>
        [JsonProperty("people")]
        public int People { get; set; }
    }

    public sealed class UserFilterValuePage
    {
        [JsonProperty("dimension")]
        public string Dimension { get; set; }

        /// <summary>Largest first, then alphabetically - the order an admin scans a picker in.</summary>
        [JsonProperty("values")]
        public List<UserFilterValueModel> Values { get; set; } = new List<UserFilterValueModel>();

        /// <summary>How many values matched the search, of which <see cref="Values"/> holds the first page.</summary>
        [JsonProperty("totalMatching")]
        public int TotalMatching { get; set; }

        /// <summary>True when more values matched than were returned, so the picker should search the server.</summary>
        [JsonProperty("truncated")]
        public bool Truncated { get; set; }

        /// <summary>How many people have no value at all - the size of the "(not set)" option.</summary>
        [JsonProperty("peopleWithoutValue")]
        public int PeopleWithoutValue { get; set; }
    }

    /// <summary>
    /// Answers the portal's two questions about a directory snapshot: which attributes can I filter on,
    /// and what values does one of them hold?
    /// </summary>
    /// <remarks>
    /// Pure functions of the snapshot, so they are unit-tested without a web server or a database, and
    /// any report that adopts the user filter can serve the same picker.
    /// </remarks>
    public static class UserFilterCatalogue
    {
        public const int DefaultTake = 200;
        public const int MaxTake = 1000;

        /// <summary>A search longer than any value could be is truncated rather than rejected.</summary>
        public const int MaxSearchLength = 200;

        public static UserFilterDimensionList ListDimensions(UserDirectorySnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));

            return new UserFilterDimensionList
            {
                People = snapshot.PeopleCount,
                LoadedUtc = snapshot.LoadedUtc,
                Dimensions = snapshot.Dimensions.Select(d => new UserFilterDimensionModel
                {
                    Key = d.Key,
                    Kind = d.Kind == UserFilterDimensionKind.Entra ? "entra" : "custom",
                    Name = d.Name,
                    OrgTypeId = d.OrgTypeId,
                    DistinctValues = d.DistinctValues,
                    PeopleWithValue = d.PeopleWithValue,
                    SupportsTextMatch = UserFilterDimensions.SupportsTextMatch(d.Key),
                    FixedValues = UserFilterDimensions.HasFixedValues(d.Key),
                }).ToList(),
            };
        }

        /// <summary>
        /// One page of a dimension's values, or <c>null</c> when the dimension does not exist.
        /// </summary>
        /// <param name="search">Matched anywhere in the value, case-insensitively. Blank for every value.</param>
        public static UserFilterValuePage ListValues(UserDirectorySnapshot snapshot, string dimension, string search, int take)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));

            var column = snapshot.Column(dimension);
            if (column == null) return null;

            var term = (search ?? string.Empty).Trim();
            if (term.Length > MaxSearchLength) term = term.Substring(0, MaxSearchLength);

            var size = take <= 0 ? DefaultTake : Math.Min(take, MaxTake);
            var fixedValues = UserFilterDimensions.FixedValues(dimension);

            if (fixedValues != null)
            {
                // A fixed-value dimension keeps its natural order - member before guest, enabled before
                // disabled - and lists every token even when nobody holds it, so the picker never loses
                // an option because a small tenant happens to have no guests.
                var tokens = fixedValues
                    .Where(t => term.Length == 0 || t.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
                    .Select(t => new UserFilterValueModel { Value = t, People = PeopleFor(column, t) })
                    .ToList();

                return new UserFilterValuePage
                {
                    Dimension = dimension,
                    Values = tokens.Take(size).ToList(),
                    TotalMatching = tokens.Count,
                    Truncated = tokens.Count > size,
                    PeopleWithoutValue = snapshot.PeopleCount - column.PeopleWithValue,
                };
            }

            // One pass over the precomputed order, keeping the first page and counting the rest - no
            // per-request sort, and no model object for a value that is not returned. A CSV organisation
            // type can legitimately hold a distinct value per person.
            var page = new List<UserFilterValueModel>(Math.Min(size, column.Values.Count));
            var total = 0;
            foreach (var index in column.OrderByPeople)
            {
                var value = column.Values[index];
                if (term.Length > 0 && value.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0) continue;

                total++;
                if (page.Count < size) page.Add(new UserFilterValueModel { Value = value, People = column.PeoplePerValue[index] });
            }

            return new UserFilterValuePage
            {
                Dimension = dimension,
                Values = page,
                TotalMatching = total,
                Truncated = total > size,
                PeopleWithoutValue = snapshot.PeopleCount - column.PeopleWithValue,
            };
        }

        private static int PeopleFor(UserDirectoryColumn column, string value)
        {
            var index = column.IndexOf(value);
            return index >= 0 ? column.PeoplePerValue[index] : 0;
        }
    }
}
