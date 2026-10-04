// @vitest-environment node
import { afterEach, describe, expect, it } from 'vitest';
import { loadCatalog, setActiveLanguage, translateStatic, type Language, type TFunction } from '../../i18n';
import type { GlobalFilterClause, GlobalFilterClauseEcho } from '../../types/globalFilter';
import { describeGlobalClause, describeGlobalFilter, globalPillValues } from './describeGlobalFilter';

async function translator(language: Language): Promise<TFunction> {
  await loadCatalog(language);
  setActiveLanguage(language);
  return (key, values) => translateStatic(language, key, values);
}

afterEach(() => setActiveLanguage('en'));

function definition(dimension: string, values: string[], overrides: Partial<GlobalFilterClause> = {}): GlobalFilterClause {
  return { join: 'and', dimension, operator: 'is', values, includeNotSet: false, viewerAttribute: null, ...overrides };
}

function echo(
  dimension: string,
  values: string[],
  overrides: Partial<GlobalFilterClauseEcho> = {},
): GlobalFilterClauseEcho {
  return { ...definition(dimension, values), viewerValue: null, unresolved: false, ...overrides };
}

describe('the global filter, read to the person it applies to', () => {
  it('fills in their own value and says where it came from', async () => {
    const t = await translator('en');

    expect(describeGlobalClause(t, echo('department', [], { viewerAttribute: 'department', viewerValue: 'Sales' }), 'reader')).toBe(
      'Department is Sales (from your profile)',
    );
    expect(
      describeGlobalClause(t, echo('userName', [], { viewerAttribute: 'userName', viewerValue: 'alex@contoso.com' }), 'reader'),
    ).toBe('User name is alex@contoso.com (you)');
    expect(
      describeGlobalClause(t, echo('manager', [], { viewerAttribute: 'manager', viewerValue: 'boss@contoso.com' }), 'reader'),
    ).toBe('Manager is boss@contoso.com (your manager)');
  });

  it('lists fixed values before the reader’s own', async () => {
    const t = await translator('en');
    expect(
      describeGlobalClause(t, echo('department', ['Finance'], { viewerAttribute: 'department', viewerValue: 'Sales' }), 'reader'),
    ).toBe('Department is Finance or Sales (from your profile)');
  });

  it('translates a product token the reader holds, and leaves tenant data as stored', async () => {
    const t = await translator('es');
    expect(describeGlobalClause(t, echo('userType', [], { viewerAttribute: 'userType', viewerValue: 'member' }), 'reader')).toBe(
      'Tipo de usuario es Miembro (según su perfil)',
    );
    expect(
      describeGlobalClause(t, echo('department', [], { viewerAttribute: 'department', viewerValue: 'Καλημέρα κόσμε' }), 'reader'),
    ).toBe('Departamento es Καλημέρα κόσμε (según su perfil)');
  });

  it('says a condition the reader has no value for matches nobody', async () => {
    const t = await translator('en');
    const missing = echo('department', [], { viewerAttribute: 'department', unresolved: true });

    expect(describeGlobalClause(t, missing, 'reader')).toBe(
      'Department must match your own, which isn’t recorded for you, so this condition matches nobody',
    );
    expect(globalPillValues(t, missing, 'reader')).toBe('not recorded for you');
  });

  it('brackets OR groups like the reader’s own filter', async () => {
    const t = await translator('en');
    const clauses = [
      echo('department', [], { viewerAttribute: 'department', viewerValue: 'Sales' }),
      echo('userType', ['member']),
      { ...echo('country', ['France']), join: 'or' as const },
    ];
    expect(describeGlobalFilter(t, clauses, 'reader')).toBe(
      '(Department is Sales (from your profile) and User type is Member) or (Country or region is France)',
    );
  });
});

describe('the global filter, read to someone who may not see who it names', () => {
  it('says whose value it is, and how many people, but never who', async () => {
    const t = await translator('en');

    expect(describeGlobalClause(t, echo('userName', [], { viewerAttribute: 'userName', viewerValueHidden: true }), 'reader')).toBe(
      'User name is you',
    );
    expect(
      describeGlobalClause(
        t,
        echo('manager', [], { hiddenValues: 2, viewerAttribute: 'manager', viewerValueHidden: true }),
        'reader',
      ),
    ).toBe('Manager is 2 named people or your manager');
    expect(
      describeGlobalClause(t, echo('managementChain', [], { viewerAttribute: 'userName', viewerValueHidden: true }), 'reader'),
    ).toBe('Management chain includes you');
    expect(describeGlobalClause(t, echo('userName', [], { operator: 'contains', hiddenValues: 1 }), 'reader')).toBe(
      'User name contains 1 search term',
    );
    expect(globalPillValues(t, echo('manager', [], { hiddenValues: 3 }), 'reader')).toBe('3 named people');
  });

  it('does not mistake a withheld value for a missing one', async () => {
    const t = await translator('en');
    const withheld = echo('manager', [], { viewerAttribute: 'manager', viewerValueHidden: true });

    expect(describeGlobalClause(t, withheld, 'reader')).not.toContain('matches nobody');
    expect(globalPillValues(t, withheld, 'reader')).toBe('your manager');
  });

  it('is translated, counts included', async () => {
    const t = await translator('es');

    expect(
      describeGlobalClause(
        t,
        echo('manager', [], { hiddenValues: 12345, viewerAttribute: 'manager', viewerValueHidden: true }),
        'reader',
      ),
    ).toBe('Responsable es 12.345 personas concretas o su responsable');
    expect(globalPillValues(t, echo('userName', [], { hiddenValues: 1 }), 'reader')).toBe('1 persona concreta');
  });
});

describe('the global filter, as the administrator wrote it', () => {
  it('names the viewer rather than anyone in particular', async () => {
    const t = await translator('en');

    expect(describeGlobalClause(t, definition('department', [], { viewerAttribute: 'department' }), 'definition')).toBe(
      'Department is the viewer’s own value',
    );
    expect(describeGlobalClause(t, definition('managementChain', [], { viewerAttribute: 'userName' }), 'definition')).toBe(
      'Management chain includes the viewer',
    );
    expect(globalPillValues(t, definition('manager', [], { viewerAttribute: 'manager' }), 'definition')).toBe(
      'the viewer’s manager',
    );
  });

  it('reads as grammatical Spanish after "incluye a"', async () => {
    const t = await translator('es');

    // "incluye a el ..." would have to contract to "al"; the phrases are worded so it never arises.
    expect(describeGlobalClause(t, definition('managementChain', [], { viewerAttribute: 'userName' }), 'definition')).toBe(
      'Cadena de responsables incluye a la persona lectora',
    );
    expect(describeGlobalClause(t, definition('managementChain', [], { viewerAttribute: 'manager' }), 'definition')).toBe(
      'Cadena de responsables incluye a la persona responsable del lector',
    );
  });

  it('summarises a long list on a pill', async () => {
    const t = await translator('en');
    expect(globalPillValues(t, definition('department', ['A', 'B', 'C'], { viewerAttribute: 'department' }), 'definition')).toBe(
      'A, B +2 more',
    );
  });
});
