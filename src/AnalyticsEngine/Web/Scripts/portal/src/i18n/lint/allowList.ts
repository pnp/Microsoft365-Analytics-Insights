/**
 * The two escape hatches for the untranslated-text check, and the rules for using them.
 *
 * Both are deliberately awkward. Adding an entry is a visible, reviewable diff with a reason
 * beside it, which is the point: the check exists so that "we forgot to translate the new panel"
 * cannot happen quietly, and an allow-list that can be extended without comment would put the
 * quiet back.
 *
 * **`ALLOWED_LITERALS` is for text that is the same in every language.** Product and brand names
 * Microsoft ships untranslated, file-format names, units and symbols. If a Spanish speaker would
 * write the same characters, it belongs here. If a Spanish speaker would write something else, it
 * belongs in the catalog - even when the English happens to be a single word.
 *
 * **`IGNORED_FILE_PATTERNS` is for files that contain no user-facing text at all**, or where the
 * text is the catalog itself. Never add a component or page here; that is how a whole area quietly
 * stops being translated.
 */

/**
 * Strings that read identically in English and Spanish.
 *
 * Mostly Microsoft product names. A Spanish-speaking M365 administrator sees "Copilot", "Teams",
 * "SharePoint" and "OneDrive" in their own admin centre, so translating them here would make the
 * portal harder to follow rather than easier - and would break the match between a label in this
 * portal and the same thing in the Microsoft 365 admin centre.
 */
export const ALLOWED_LITERALS = new Set<string>([
  // Microsoft product and service names, untranslated in Spanish by Microsoft.
  'Microsoft',
  'Microsoft 365',
  'Microsoft 365 Advanced Analytics',
  'Microsoft 365 Copilot',
  'Microsoft Teams',
  'Copilot',
  'Copilot Chat',
  'Copilot Studio',
  // The product's own name for agent-assisted work, used as a feature name throughout the Copilot
  // Adoption page. Not an English word being left untranslated - it is the name of the thing.
  'Cowork',
  'Teams',
  'SharePoint',
  'SharePoint Online',
  'OneDrive',
  'Outlook',
  'Exchange',
  'Exchange Online',
  'Power Apps',
  'Power Automate',
  'Power BI',
  'Power Platform',
  'Viva',
  'Viva Engage',
  'Yammer',
  'Azure',
  'Azure OpenAI',
  'Entra',
  'Microsoft Entra',
  'Microsoft Graph',
  'Graph',
  'Word',
  'Excel',
  'PowerPoint',
  'OneNote',
  'Loop',
  'Planner',
  'Whiteboard',
  'Stream',
  'Forms',
  'Visio',
  'Windows',
  'Office',
  'Microsoft 365 apps',

  // Technical identifiers and formats that are not translated in either language.
  'SQL',
  'SQL Server',
  'Azure SQL',
  'Redis',
  'API',
  'CSV',
  'JSON',
  'XLSX',
  'PDF',
  'HTTP',
  'HTTPS',
  'URL',
  'URI',
  'GUID',
  'UPN',
  'DLP',
  'RBAC',
  'UTC',
  'SKU',
  'AI',
  'ID',
  'IP',
  'DNS',
  'App Insights',
  'Application Insights',
  'Service Bus',
  'Cognitive Services',
  'OpenAI',
  'Webhook',

  // Symbols, punctuation and units that carry no language.
  '\u2014',
  '\u2013',
  '\u2026',
  'n/a',
  'N/A',
  'OK',
  // Spelled the same in Spanish. "No" is the Spanish for "No"; "Total" and "Error" are the same
  // word in both languages.
  'No',
  'Total',
  'Error',
]);

/**
 * Files the check does not read.
 *
 * Every entry is either the catalog itself (where English text is the whole point), test code, or
 * a module with no rendered text in it.
 */
export const IGNORED_FILE_PATTERNS: RegExp[] = [
  // The catalog and the checker that reads it.
  /^i18n\//,
  // Tests assert on English text on purpose, and fixtures carry synthetic data.
  /\.test\.tsx?$/,
  /^test\//,
  // API response shapes and request/response plumbing: types and fetch calls, no rendered text.
  /^types\//,
  // Vite/TypeScript ambient declarations.
  /^vite-env\.d\.ts$/,
  /^global\.d\.ts$/,
];
