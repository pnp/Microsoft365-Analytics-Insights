import type { UserOrgCsvUnusableRow } from '../../types/userOrgs';

export interface UnusableRowsCsvText {
  lineHeader: string;
  userHeader: string;
  valueHeader: string;
  reasonHeader: string;
}

function safeCell(value: string): string {
  const neutralised = /^[=+\-@\t\r]/.test(value) ? `'${value}` : value;
  return /[",\r\n]/.test(neutralised) ? `"${neutralised.replace(/"/g, '""')}"` : neutralised;
}

/** Builds the Excel-friendly UTF-8 CSV listing rows the server refused to import. */
export function buildUnusableRowsCsv(
  rows: UserOrgCsvUnusableRow[],
  text: UnusableRowsCsvText,
  reasonForCode: (code: string) => string,
): string {
  const header = [text.lineHeader, text.userHeader, text.valueHeader, text.reasonHeader].map(safeCell);
  const body = rows.map((row) =>
    [
      String(row.lineNumber),
      row.upn ?? '',
      row.orgValue ?? '',
      reasonForCode(row.code),
    ].map(safeCell).join(','),
  );

  return `\uFEFF${[header.join(','), ...body].join('\r\n')}\r\n`;
}

export function unusableRowsFileName(originalName: string | null | undefined, fallbackBase: string): string {
  const base = (originalName?.trim() || fallbackBase).replace(/\.[^.\\\/]*$/, '');
  return `${base}-rows-to-fix.csv`;
}
