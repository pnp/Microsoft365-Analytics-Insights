import type { UserOrgCsvUnusableRow } from '../../types/userOrgs';

export interface UnusableRowsCsvText {
  lineHeader: string;
  userHeader: string;
  valueHeader: string;
  reasonHeader: string;
}

export function csvCell(value: string): string {
  const neutralised = /^[=+\-@\t\r]/.test(value) ? `'${value}` : value;
  return /[",\r\n]/.test(neutralised) ? `"${neutralised.replace(/"/g, '""')}"` : neutralised;
}

/** A whole CSV document - UTF-8 with a byte order mark, so Excel reads accents and non-Latin names correctly. */
export function csvDocument(rows: string[][]): string {
  return `\uFEFF${rows.map((row) => row.map(csvCell).join(',')).join('\r\n')}\r\n`;
}

/** Builds the Excel-friendly UTF-8 CSV listing rows the server refused to import. */
export function buildUnusableRowsCsv(
  rows: UserOrgCsvUnusableRow[],
  text: UnusableRowsCsvText,
  reasonForCode: (code: string) => string,
): string {
  return csvDocument([
    [text.lineHeader, text.userHeader, text.valueHeader, text.reasonHeader],
    ...rows.map((row) => [String(row.lineNumber), row.upn ?? '', row.orgValue ?? '', reasonForCode(row.code)]),
  ]);
}

/** A download name derived from the uploaded file's, with a suffix saying what it is. */
export function derivedFileName(originalName: string | null | undefined, fallbackBase: string, suffix: string): string {
  const base = (originalName?.trim() || fallbackBase).replace(/\.[^.\\\/]*$/, '');
  return `${base}-${suffix}.csv`;
}

export function unusableRowsFileName(originalName: string | null | undefined, fallbackBase: string): string {
  return derivedFileName(originalName, fallbackBase, 'rows-to-fix');
}

/** Hands a CSV document to the browser as a download. */
export function downloadCsv(csv: string, fileName: string): void {
  const url = URL.createObjectURL(new Blob([csv], { type: 'text/csv;charset=utf-8' }));
  const link = document.createElement('a');
  link.href = url;
  link.download = fileName;
  document.body.append(link);
  link.click();
  link.remove();
  URL.revokeObjectURL(url);
}
