import { describe, expect, it } from 'vitest';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { screen } from '@testing-library/react';
import { renderWithProvider } from '../../test/renderWithProvider';
import { EN_CATALOG } from '../../i18n/catalog';
import es from '../../i18n/catalog/es/userOrgs';
import CsvFileFormat from './CsvFileFormat';

/** The parser the example has to satisfy, read from the C# so the two cannot drift apart. */
const PARSER_SOURCE = join(process.cwd(), '..', '..', '..', 'Common', 'Entities', 'UserOrgs', 'UserOrgCsvParser.cs');

function stringArray(source: string, name: string): string[] {
  const match = new RegExp(`${name}\\s*=\\s*\\{([^}]*)\\}`).exec(source);
  expect(match, `Could not find ${name} in UserOrgCsvParser.cs`).not.toBeNull();
  return [...match![1].matchAll(/"([^"]*)"/g)].map((m) => m[1]);
}

function exampleLines(): string[] {
  return (screen.getByLabelText('Example file').textContent ?? '').split('\n');
}

describe('CsvFileFormat', () => {
  it('shows an example file whose value column is named after the type', () => {
    renderWithProvider(<CsvFileFormat typeName="Cost centre" />);

    expect(screen.getByText('What the file should look like')).toBeVisible();
    const lines = exampleLines();
    expect(lines[0]).toBe('UserPrincipalName,Cost centre');
    expect(lines).toHaveLength(4);
    expect(lines[3]).toBe('adele.vance@contoso.com,');
    expect(screen.getByText(/like the last one above, clears/)).toBeVisible();
  });

  it('quotes a type name that would otherwise split the header row', () => {
    renderWithProvider(<CsvFileFormat typeName={'Region, "EMEA"'} />);

    expect(exampleLines()[0]).toBe('UserPrincipalName,"Region, ""EMEA"""');
  });

  it('uses a generic header until the type has a name', () => {
    renderWithProvider(<CsvFileFormat typeName="   " />);

    expect(exampleLines()[0]).toBe('UserPrincipalName,Organisation');
  });

  it('folds behind a disclosure on a card the admin comes back to', () => {
    const { container } = renderWithProvider(<CsvFileFormat typeName="Cost centre" collapsed />);

    const details = container.querySelector('details');
    expect(details).not.toBeNull();
    expect(details!.open).toBe(false);
    expect(details!.querySelector('summary')?.textContent).toBe('What the file should look like');
  });
});

describe('the CSV example, checked against the parser', () => {
  const parser = readFileSync(PARSER_SOURCE, 'utf8');
  const upnHeaders = stringArray(parser, 'UpnHeaderNames');
  const delimiters = /CandidateDelimiters\s*=\s*\{([^}]*)\}/.exec(parser)?.[1] ?? '';

  it('reads the parser source', () => {
    expect(upnHeaders.length).toBeGreaterThan(0);
  });

  it.each([
    ['en', EN_CATALOG['userOrgs.csvFormat.example']],
    ['es', es['userOrgs.csvFormat.example']],
  ])('opens with a user column header the parser recognises (%s)', (_language, example) => {
    const header = example.split('\n')[0].split(',')[0];
    expect(upnHeaders).toContain(header.trim().toLowerCase());

    // Every data row's user is a user principal name, and the file ends on the row that clears one.
    const rows = example.split('\n').slice(1);
    expect(rows.every((row) => /^[^,@\s]+@[^,\s]+,/.test(row))).toBe(true);
    expect(rows[rows.length - 1].endsWith(',')).toBe(true);
  });

  it('names only header spellings the parser recognises', () => {
    for (const named of ['UserPrincipalName', 'UPN', 'User', 'Email']) {
      expect(upnHeaders).toContain(named.toLowerCase());
      expect(EN_CATALOG['userOrgs.csvFormat.ruleHeader']).toContain(named);
      expect(es['userOrgs.csvFormat.ruleHeader']).toContain(named);
    }
  });

  it('lists the separators the parser detects', () => {
    for (const separator of ["','", "';'", "'\\t'", "'|'"]) {
      expect(delimiters).toContain(separator);
    }
  });
});
