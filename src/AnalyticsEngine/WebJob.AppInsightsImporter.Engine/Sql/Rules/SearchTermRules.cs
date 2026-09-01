using WebJob.AppInsightsImporter.Engine.APIResponseParsers.CustomEvents;

namespace WebJob.AppInsightsImporter.Engine.Sql.Rules
{
    /// <summary>
    /// Pure rules for the search-term staging path - no SQL, no ADO.NET, no logging.
    /// Modelled on ActivityAPI/Loaders/AuditLogContentDispatcher, which keeps routing/filter decisions
    /// separate from I/O. See issue #369.
    /// </summary>
    public static class SearchTermRules
    {
        /// <summary>
        /// Width of the [search_term] staging parameter, which mirrors the target column.
        /// </summary>
        public const int MaxSearchTermLength = 250;

        private const string Ellipsis = "...";

        /// <summary>
        /// Whether a search event carries enough to stage a row.
        ///
        /// On the API path this is defence in depth rather than a behaviour change: the parser only ever
        /// adds an event to the collection when <c>IsValid</c> holds (<c>AppInsightsQueryResultCollection</c>
        /// calls <c>Rows.Add</c> solely on a non-null <c>Build(...)</c> result, and
        /// <c>CustomEventsResultCollection.Build</c> returns null unless <c>e.IsValid</c>), and
        /// <see cref="SearchEventAppInsightsQueryResult.IsValid"/> already requires a non-empty SearchText.
        ///
        /// It matters because <c>Rows</c> is a public settable list, so a collection can be populated by
        /// other means. The save path only selected events by type and never re-checked validity, then
        /// went straight to <c>searchTerm.Length</c> - so a null SearchText threw a NullReferenceException
        /// that took the entire Searches section down with it.
        ///
        /// Empty (as opposed to null) is rejected for the same reason <c>IsValid</c> rejects it; note that
        /// a whitespace-only term still stages, exactly as before, since this is IsNullOrEmpty and not
        /// IsNullOrWhiteSpace.
        /// </summary>
        public static bool ShouldStage(SearchEventAppInsightsQueryResult searchEvent)
        {
            return !string.IsNullOrEmpty(searchEvent?.CustomProperties?.SearchText);
        }

        /// <summary>
        /// Truncate a search term to <see cref="MaxSearchTermLength"/> characters, preserving the
        /// original behaviour of replacing the tail with an ellipsis.
        ///
        /// Counting is by UTF-16 char, which is what both <c>string.Length</c> and the
        /// <c>nvarchar(250)</c> parameter measure - so a Greek term is limited to 250 characters, not
        /// 250 bytes. The one refinement over the original <c>Substring(0, 247)</c> is that the cut is
        /// not allowed to land between a surrogate pair (e.g. an emoji), which would otherwise store a
        /// lone surrogate: a string SQL Server accepts but which is not valid UTF-16.
        /// </summary>
        public static string Truncate(string searchText)
        {
            if (string.IsNullOrEmpty(searchText) || searchText.Length <= MaxSearchTermLength)
            {
                return searchText;
            }

            var cut = MaxSearchTermLength - Ellipsis.Length;

            // Never split a surrogate pair: if the last kept char is a high surrogate, its low partner
            // would be cut away, leaving an unpaired surrogate.
            if (char.IsHighSurrogate(searchText[cut - 1]))
            {
                cut--;
            }

            return searchText.Substring(0, cut) + Ellipsis;
        }
    }
}
