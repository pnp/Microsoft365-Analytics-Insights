// @vitest-environment node
import { describe, expect, it } from 'vitest';
import type { GlobalFilterClause, GlobalFilterDefinition } from '../../types/globalFilter';
import {
  fromClauseModels,
  globalClauseIsComplete,
  globalFilterProblem,
  isEmptyGlobalFilter,
  removeGlobalClause,
  sameGlobalFilter,
  serializeGlobalFilter,
  setGlobalJoin,
  viewerAttributesFor,
  viewerKind,
} from './globalFilterModel';

function clause(dimension: string, values: string[], overrides: Partial<GlobalFilterClause> = {}): GlobalFilterClause {
  return { join: 'and', dimension, operator: 'is', values, includeNotSet: false, viewerAttribute: null, ...overrides };
}

describe('which of the viewer’s attributes a condition may compare with', () => {
  it('mirrors GlobalFilterViewerAttributes.AllowedFor on the server', () => {
    expect(viewerAttributesFor('department')).toEqual(['department']);
    expect(viewerAttributesFor('emailDomain')).toEqual(['emailDomain']);
    expect(viewerAttributesFor('org:12')).toEqual(['org:12']);
    expect(viewerAttributesFor('userName')).toEqual(['userName']);
    expect(viewerAttributesFor('manager')).toEqual(['userName', 'manager']);
    expect(viewerAttributesFor('managementChain')).toEqual(['userName', 'manager']);
  });

  it('offers nothing for a key the server would not read', () => {
    expect(viewerAttributesFor('')).toEqual([]);
    expect(viewerAttributesFor('salary')).toEqual([]);
    expect(viewerAttributesFor('org:0')).toEqual([]);
  });

  it('names the viewer, their manager, or their own value', () => {
    expect(viewerKind('userName')).toBe('self');
    expect(viewerKind('manager')).toBe('manager');
    expect(viewerKind('department')).toBe('own');
    expect(viewerKind('org:3')).toBe('own');
  });
});

describe('a complete condition', () => {
  it('may have the viewer’s own value and nothing else', () => {
    expect(globalClauseIsComplete(clause('department', [], { viewerAttribute: 'department' }))).toBe(true);
    expect(globalClauseIsComplete(clause('department', []))).toBe(false);
    expect(globalClauseIsComplete(clause('department', [], { includeNotSet: true }))).toBe(true);
    expect(isEmptyGlobalFilter({ clauses: [clause('department', [])] })).toBe(true);
    expect(isEmptyGlobalFilter({ clauses: [clause('department', [], { viewerAttribute: 'department' })] })).toBe(false);
  });
});

describe('the wire form GlobalFilterCodec reads', () => {
  it('adds vu for a condition on the viewer and leaves defaults out', () => {
    const filter: GlobalFilterDefinition = {
      clauses: [
        clause('department', [], { viewerAttribute: 'department' }),
        clause('userType', ['member']),
        clause('country', ['France'], { join: 'or', operator: 'isNot', includeNotSet: true }),
      ],
    };

    expect(JSON.parse(serializeGlobalFilter(filter))).toEqual([
      { d: 'department', v: [], vu: 'department' },
      { d: 'userType', v: ['member'] },
      { j: 'or', d: 'country', op: 'isNot', v: ['France'], n: true },
    ]);
  });

  it('leaves incomplete conditions out, and is empty - which removes the filter - when none is left', () => {
    expect(serializeGlobalFilter({ clauses: [] })).toBe('');
    expect(serializeGlobalFilter({ clauses: [clause('department', [])] })).toBe('');
    expect(serializeGlobalFilter(null)).toBe('');
  });

  it('treats the same conditions as the same filter', () => {
    const a = { clauses: [clause('department', ['Sales'])] };
    const b = fromClauseModels([{ ...clause('department', ['Sales']), join: 'or' }]);
    expect(sameGlobalFilter(a, b)).toBe(true);
    expect(sameGlobalFilter(a, { clauses: [clause('department', ['Sales'], { viewerAttribute: 'department' })] })).toBe(false);
  });
});

describe('editing the conditions', () => {
  it('reads the server’s models with the first join as AND and a blank viewer attribute as none', () => {
    const filter = fromClauseModels([
      { ...clause('department', ['Sales']), join: 'or', viewerAttribute: '' as unknown as null },
    ]);
    expect(filter.clauses[0].join).toBe('and');
    expect(filter.clauses[0].viewerAttribute).toBeNull();
  });

  it('keeps every other condition in its group when one is removed', () => {
    // (Sales) or (UK and London): removing the condition that opens the OR group must not AND London to Sales.
    const filter: GlobalFilterDefinition = {
      clauses: [
        clause('department', ['Sales']),
        clause('country', ['UK'], { join: 'or' }),
        clause('officeLocation', ['London']),
      ],
    };

    const after = removeGlobalClause(filter, 1);
    expect(after.clauses.map((c) => [c.dimension, c.join])).toEqual([
      ['department', 'and'],
      ['officeLocation', 'or'],
    ]);
  });

  it('never leaves an OR on the first condition', () => {
    const filter = setGlobalJoin({ clauses: [clause('department', ['Sales'])] }, 0, 'or');
    expect(filter.clauses[0].join).toBe('and');
  });
});

describe('what the server would refuse', () => {
  it('refuses a text search on the viewer’s own value', () => {
    expect(
      globalFilterProblem({ clauses: [clause('department', ['Sal'], { operator: 'contains', viewerAttribute: 'department' })] }),
    ).toBe('viewerText');
  });

  it('refuses more conditions, values or text than the server accepts', () => {
    expect(globalFilterProblem({ clauses: Array.from({ length: 26 }, () => clause('department', ['Sales'])) })).toBe('tooManyClauses');
    expect(globalFilterProblem({ clauses: [clause('department', Array.from({ length: 501 }, (_, i) => `D${i}`))] })).toBe('tooManyValues');
    expect(globalFilterProblem({ clauses: [clause('department', ['x'.repeat(849)])] })).toBe('valueTooLong');
    expect(
      globalFilterProblem({ clauses: [clause('userName', Array.from({ length: 11 }, (_, i) => `t${i}`), { operator: 'contains' })] }),
    ).toBe('tooManyTerms');
  });

  it('refuses a definition longer than the server reads', () => {
    const values = Array.from({ length: 400 }, (_, i) => `Department number ${i}`);
    expect(globalFilterProblem({ clauses: [clause('department', values)] })).toBe('tooLong');
  });

  it('accepts an ordinary filter', () => {
    expect(globalFilterProblem({ clauses: [clause('department', [], { viewerAttribute: 'department' })] })).toBeNull();
  });
});
