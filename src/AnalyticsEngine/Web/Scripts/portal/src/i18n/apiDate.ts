/**
 * Reading a date the portal's API sent.
 *
 * Every date the reporting API sends means UTC: usage days and week starts are UTC calendar days,
 * and instants are UTC. But a `DateTime` read from SQL has lost its kind, and Web API's default
 * Json.NET settings then write it with no offset at all - `"2026-10-19T00:00:00"`, not
 * `"2026-10-19T00:00:00Z"`. ECMAScript reads a date-time with no offset as the browser's **local**
 * time, so `new Date()` on that string is local midnight. For every reader east of UTC - every
 * Spanish reader all year, every British one in summer - local midnight is the previous day in UTC,
 * and a helper that then formats in UTC prints the day before: a Monday week start reads as Sunday.
 *
 * {@link parseApiDate} reads a date-time with no offset as UTC. Everything else is passed to
 * `new Date` unchanged: a value that carries `Z` or an offset already says what it means, a
 * date-only `YYYY-MM-DD` is already UTC, and an empty or malformed string stays exactly as invalid
 * as it was.
 *
 * Parse every date string from the API with it, even one the server already stamps with `Z`: it
 * costs nothing there, and a caller cannot see from the call site whether a new endpoint stamps its
 * dates. `src/i18n/lint/apiDateParsing.test.ts` fails on a date string parsed any other way.
 *
 * It is not for a value that genuinely is local wall-clock time with its zone lost. The install
 * log's `dateApplied` is `DateTime.Now` on whichever machine ran the installer; nothing records
 * that machine's zone, so reading it as UTC would move it by an offset that cannot be recovered.
 */

/** An ISO 8601 date-time with no `Z` and no offset, as Json.NET writes a `DateTime` read from SQL. */
const ZONELESS_DATE_TIME = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(?::\d{2}(?:\.\d+)?)?$/;

/** A date the API sent, as a moment in time; a date-time with no offset is read as UTC. */
export function parseApiDate(value: string): Date {
  return new Date(ZONELESS_DATE_TIME.test(value) ? `${value}Z` : value);
}
