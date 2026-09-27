import { afterEach, describe, expect, it } from 'vitest';
import { loadCatalog, setActiveLanguage, translateStatic, type Language, type TFunction } from '../../i18n';
import type { UserFilter, UserFilterClause } from '../../types/userFilter';
import { describeClause, describeUserFilter, dimensionLabel, joinConditions, operatorShortLabel } from './describeUserFilter';

function clause(dimension: string, values: string[], overrides: Partial<UserFilterClause> = {}): UserFilterClause {
  return { join: 'and', dimension, operator: 'is', values, includeNotSet: false, ...overrides };
}

async function translator(language: Language): Promise<TFunction> {
  await loadCatalog(language);
  // Lists ("A, B or C") are joined in the active locale, as they are on the page.
  setActiveLanguage(language);
  return (key, values) => translateStatic(language, key, values);
}

afterEach(() => setActiveLanguage('en'));

const names = { names: { 'org:4': 'Cost centre' } };

describe('describing a filter in English', () => {
  it('reads one condition as a plain sentence', async () => {
    const t = await translator('en');

    expect(describeClause(t, clause('department', ['Sales']), 'Department')).toBe('Department is Sales');
    expect(describeClause(t, clause('department', ['Sales', 'Marketing', 'Finance']), 'Department')).toBe(
      'Department is Sales, Marketing or Finance',
    );
    expect(describeClause(t, clause('department', ['Sales'], { operator: 'isNot' }), 'Department')).toBe(
      'Department is not Sales',
    );
  });

  it('never reads as a double negative when "not set" is involved', async () => {
    const t = await translator('en');

    expect(describeClause(t, clause('department', [], { includeNotSet: true }), 'Department')).toBe('Department is not set');
    expect(describeClause(t, clause('department', [], { includeNotSet: true, operator: 'isNot' }), 'Department')).toBe(
      'Department is set',
    );
    expect(describeClause(t, clause('department', ['Sales'], { includeNotSet: true }), 'Department')).toBe(
      'Department is Sales or not set',
    );
    expect(describeClause(t, clause('department', ['Sales'], { includeNotSet: true, operator: 'isNot' }), 'Department')).toBe(
      'Department is set and is not Sales',
    );
  });

  it('quotes the text a "contains" condition looks for', async () => {
    const t = await translator('en');

    expect(describeClause(t, clause('jobTitle', ['engineer', 'developer'], { operator: 'contains' }), 'Job title')).toBe(
      'Job title contains “engineer” or “developer”',
    );
    expect(describeClause(t, clause('jobTitle', ['intern'], { operator: 'notContains' }), 'Job title')).toBe(
      'Job title does not contain “intern”',
    );
  });

  it('reads the management chain as "includes", which is what it means', async () => {
    const t = await translator('en');

    expect(describeClause(t, clause('managementChain', ['ceo@contoso.com']), 'Management chain')).toBe(
      'Management chain includes ceo@contoso.com',
    );
    expect(operatorShortLabel(t, 'managementChain', 'isNot')).toBe('does not include');
    expect(operatorShortLabel(t, 'department', 'isNot')).toBe('≠');
  });

  it('translates the fixed values and leaves tenant data exactly as stored', async () => {
    const t = await translator('en');

    expect(describeClause(t, clause('userType', ['guest']), 'User type')).toBe('User type is Guest');
    expect(describeClause(t, clause('org:4', ['CC-100 Αθήνα']), 'Cost centre')).toBe('Cost centre is CC-100 Αθήνα');
  });

  it('labels a custom organisation with the administrator’s name for it, and says so when it has gone', async () => {
    const t = await translator('en');

    expect(dimensionLabel(t, 'org:4', names)).toBe('Cost centre');
    expect(dimensionLabel(t, 'org:4', { dimensions: [{ key: 'org:4', kind: 'custom', name: 'Programme', orgTypeId: 4, distinctValues: 1, peopleWithValue: 1, supportsTextMatch: true, fixedValues: false }] })).toBe('Programme');
    expect(dimensionLabel(t, 'org:99', names)).toBe('Unavailable attribute');
    expect(dimensionLabel(t, 'department')).toBe('Department');
  });

  it('brackets every group once there is more than one, so each "or" is unambiguous', async () => {
    const t = await translator('en');
    const filter: UserFilter = {
      clauses: [
        clause('department', ['Sales']),
        clause('country', ['United Kingdom']),
        clause('org:4', ['CC-12'], { join: 'or', includeNotSet: true }),
      ],
    };

    expect(describeUserFilter(t, filter, names)).toBe(
      '(Department is Sales and Country or region is United Kingdom) or (Cost centre is CC-12 or not set)',
    );
    expect(describeUserFilter(t, { clauses: filter.clauses.slice(0, 2) }, names)).toBe(
      'Department is Sales and Country or region is United Kingdom',
    );
  });

  it('joins conditions through a whole-sentence template rather than glued fragments', async () => {
    const t = await translator('en');

    expect(joinConditions(t, ['A', 'B', 'C'], 'and')).toBe('A and B and C');
    expect(joinConditions(t, ['A'], 'or')).toBe('A');
    expect(joinConditions(t, [], 'or')).toBe('');
  });
});

describe('describing a filter in Spanish', () => {
  it('translates the product’s words and nothing else', async () => {
    const t = await translator('es');
    const filter: UserFilter = {
      clauses: [
        clause('department', ['Sales', 'Marketing']),
        clause('accountStatus', ['disabled']),
        clause('org:4', ['CC-12'], { join: 'or' }),
      ],
    };

    expect(describeUserFilter(t, filter, names)).toBe(
      '(Departamento es Sales o Marketing y Estado de la cuenta es Deshabilitada) o (Cost centre es CC-12)',
    );
  });

  it('keeps "not set" free of gender agreement, whatever the attribute', async () => {
    const t = await translator('es');

    expect(describeClause(t, clause('companyName', [], { includeNotSet: true }), dimensionLabel(t, 'companyName'))).toBe(
      'Empresa sin definir',
    );
    expect(describeClause(t, clause('managementChain', ['ceo@contoso.com']), dimensionLabel(t, 'managementChain'))).toBe(
      'Cadena de responsables incluye a ceo@contoso.com',
    );
  });
});
