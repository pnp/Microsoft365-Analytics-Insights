import type { AgentCostDetailRow, AzureDimension, CreditDimension } from '../../types/agentCosts';

/** Shown for a dimension the billing API did not report for a row. */
export const NOT_REPORTED = 'Not reported';

/** Em dash, for an absent value in a dense table cell. */
export const DASH = '\u2014';

/**
 * Credits to a sensible number of places.
 *
 * Copilot Credits are fractional and a single slice can be a small fraction of one, so rounding to
 * whole numbers would show real spend as "0". Large totals get thousands separators and no decimals,
 * because six decimal places on a five-figure total is noise.
 */
export function formatCredits(value: number | null | undefined): string {
  if (value == null) return DASH;
  if (value === 0) return '0';
  if (Math.abs(value) >= 1000) return value.toLocaleString(undefined, { maximumFractionDigits: 0 });
  if (Math.abs(value) >= 1) return value.toLocaleString(undefined, { maximumFractionDigits: 2 });
  return value.toLocaleString(undefined, { maximumFractionDigits: 6 });
}

/**
 * Money in its billing currency.
 *
 * The currency code is always shown. A tenant can be billed in more than one currency, and an amount
 * without its unit invites someone to add two of them together.
 */
export function formatMoney(value: number | null | undefined, currency: string | null | undefined): string {
  if (value == null) return DASH;

  const code = currency?.trim();
  const amount = value.toLocaleString(undefined, {
    minimumFractionDigits: 2,
    maximumFractionDigits: Math.abs(value) < 1 ? 4 : 2,
  });

  return code ? `${amount} ${code}` : amount;
}

export function formatCount(value: number | null | undefined): string {
  return value == null ? DASH : Math.round(value).toLocaleString();
}

/** A UTC ISO date as a short date. Rendered in UTC - the underlying grain is a UTC usage day. */
export function formatDay(iso: string | null | undefined): string {
  if (!iso) return DASH;
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return DASH;
  return date.toLocaleDateString(undefined, { day: 'numeric', month: 'short', year: 'numeric', timeZone: 'UTC' });
}

export function formatDateTime(iso: string | null | undefined): string {
  if (!iso) return DASH;
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return DASH;
  return date.toLocaleString(undefined, {
    day: 'numeric',
    month: 'short',
    year: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
    timeZone: 'UTC',
  });
}

/** `yyyy-MM-dd` for a Date, in UTC, which is what the API expects. */
export function toIsoDay(date: Date): string {
  return date.toISOString().slice(0, 10);
}

/** The inclusive window ending today and covering `days` days. */
export function windowOfDays(days: number, now: Date = new Date()): { from: string; to: string } {
  const to = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate()));
  const from = new Date(to);
  from.setUTCDate(from.getUTCDate() - (days - 1));
  return { from: toIsoDay(from), to: toIsoDay(to) };
}

/** Human labels for the credit pivot. */
export const CREDIT_DIMENSIONS: { key: CreditDimension; label: string; hint: string }[] = [
  { key: 'agent', label: 'Agent', hint: 'Which agent the credits were billed against.' },
  { key: 'environment', label: 'Environment', hint: 'The Power Platform environment the agent lives in.' },
  {
    key: 'harness',
    label: 'Harness',
    hint: 'Standard / Copilot Chat or GitHub Copilot. Inferred from the billing feature, because Microsoft does not report it directly.',
  },
  {
    key: 'feature',
    label: 'Billing feature',
    hint: 'What was charged for - a generative answer, tenant graph grounding, an agent action.',
  },
  { key: 'model', label: 'AI model', hint: 'The model that served the request, where Microsoft reported one.' },
  { key: 'tool', label: 'Tool invoked', hint: 'The tool or connector the agent called.' },
  { key: 'knowledge', label: 'Knowledge source', hint: 'The knowledge source the answer was grounded on.' },
  { key: 'channel', label: 'Channel', hint: 'Where the agent was used from - Teams, a website, and so on.' },
];

export const AZURE_DIMENSIONS: { key: AzureDimension; label: string }[] = [
  { key: 'meter', label: 'Meter' },
  { key: 'service', label: 'Service' },
  { key: 'category', label: 'Meter category' },
  { key: 'resource', label: 'Resource' },
  { key: 'resourcegroup', label: 'Resource group' },
  { key: 'subscription', label: 'Subscription' },
];

/** Quotes a CSV field, doubling any embedded quotes. */
function csvCell(value: string | number | null | undefined): string {
  if (value == null) return '';
  const text = String(value);
  return /[",\r\n]/.test(text) ? `"${text.replace(/"/g, '""')}"` : text;
}

/**
 * Builds a CSV of the granular rows currently on screen.
 *
 * Exported client-side from the rows already fetched, so it always matches exactly what the admin can
 * see - there is no second query that could return different figures than the table they are looking at.
 */
export function detailRowsToCsv(rows: AgentCostDetailRow[]): string {
  const header = [
    'Usage date',
    'Agent',
    'Agent ID',
    'Environment',
    'Environment ID',
    'Harness',
    'Billing feature',
    'AI model',
    'Tool invoked',
    'Knowledge source',
    'Channel',
    'Billed credits',
    'Non-billed credits',
    'Distinct users',
  ];

  const lines = rows.map((r) =>
    [
      r.usageDate ? r.usageDate.slice(0, 10) : '',
      r.agentName ?? '',
      r.agentId ?? '',
      r.environmentName ?? '',
      r.environmentId ?? '',
      r.harness ?? '',
      r.featureName ?? '',
      r.llmModel ?? '',
      r.toolInvoked ?? '',
      r.knowledgeSources ?? '',
      r.channelId ?? '',
      r.billedCredits,
      r.nonBilledCredits ?? '',
      r.distinctUsers ?? '',
    ]
      .map(csvCell)
      .join(','),
  );

  return [header.map(csvCell).join(','), ...lines].join('\r\n');
}

/** Saves text to disk via a transient object URL, without navigating away. */
export function saveCsv(csv: string, filename: string): void {
  // The BOM makes Excel open a UTF-8 CSV correctly. Agent names can be non-Latin, and without it
  // Excel guesses the local code page and mangles them.
  const blob = new Blob(['\ufeff', csv], { type: 'text/csv;charset=utf-8;' });
  const url = URL.createObjectURL(blob);
  try {
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = filename;
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
  } finally {
    URL.revokeObjectURL(url);
  }
}
