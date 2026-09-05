import { describe, it, expect } from 'vitest';
import {
  mergeDemographicOptions,
  EMPTY_CATALOGUE,
  DEMOGRAPHIC_OPTION_CAP,
  type DemographicCatalogue,
  type DemographicOption,
} from './demographicOptions';

/** A bounded (<=50) backend reply: `count` groups starting at id `base`. */
function reply(base: number, count = 50, prefix = 'G'): DemographicOption[] {
  return Array.from({ length: count }, (_, i) => ({ id: base + i, name: `${prefix}${base + i}` }));
}

const ids = (c: DemographicCatalogue): number[] => c.options.map((o) => o.id).sort((a, b) => a - b);

describe('mergeDemographicOptions', () => {
  it('unions bounded replies under the cap so you can switch between groups, and takes the newest name', () => {
    let cat = EMPTY_CATALOGUE;

    // Unfiltered reply with two groups (one Unicode) -> both are options.
    cat = mergeDemographicOptions(
      cat,
      [
        { id: 1, name: 'Sales' },
        { id: 2, name: '\u039c\u03b7\u03c7\u03b1\u03bd\u03b9\u03ba\u03bf\u03af' }, // Μηχανικοί
      ],
      { unfilteredForDimension: true, selectedId: null },
    );
    expect(new Set(ids(cat))).toEqual(new Set([1, 2]));
    expect(cat.truncated).toBe(false);

    // A scoped reply for id 1 (contains ONLY id 1) still leaves id 2 selectable - the X->Y case.
    cat = mergeDemographicOptions(cat, [{ id: 1, name: 'Sales' }], { unfilteredForDimension: false, selectedId: 1 });
    expect(cat.options.some((o) => o.id === 2)).toBe(true);

    // A newer name for id 2 wins (Unicode preserved).
    cat = mergeDemographicOptions(
      cat,
      [{ id: 2, name: '\u039c\u03b7\u03c7\u03b1\u03bd\u03b9\u03ba\u03bf\u03af \u0391\u0395' }], // Μηχανικοί ΑΕ
      { unfilteredForDimension: false, selectedId: 2 },
    );
    expect(cat.options.find((o) => o.id === 2)!.name).toBe('\u039c\u03b7\u03c7\u03b1\u03bd\u03b9\u03ba\u03bf\u03af \u0391\u0395');
    expect(cat.truncated).toBe(false);
  });

  it('never exceeds the cap across many bounded replies, and signals the truncation', () => {
    let cat = EMPTY_CATALOGUE;
    cat = mergeDemographicOptions(cat, reply(0), { unfilteredForDimension: true, selectedId: null });
    expect(cat.options.length).toBe(50);
    expect(cat.truncated).toBe(false);

    // Second disjoint reply of 50 -> exactly 100 distinct, still fits (no eviction yet).
    cat = mergeDemographicOptions(cat, reply(100), { unfilteredForDimension: false, selectedId: null });
    expect(cat.options.length).toBe(DEMOGRAPHIC_OPTION_CAP);
    expect(cat.truncated).toBe(false);

    // Third disjoint reply of 50 -> 150 distinct, must evict down to the cap and flag it.
    cat = mergeDemographicOptions(cat, reply(200), { unfilteredForDimension: false, selectedId: null });
    expect(cat.options.length).toBe(DEMOGRAPHIC_OPTION_CAP);
    expect(cat.options.length).toBeLessThanOrEqual(100);
    expect(cat.truncated).toBe(true);

    // Truncation is sticky: a later fully-overlapping reply does not clear the signal.
    cat = mergeDemographicOptions(cat, reply(0), { unfilteredForDimension: false, selectedId: null });
    expect(cat.truncated).toBe(true);
  });

  it('keeps the latest unfiltered catalogue and the current selection even when evicting to the cap', () => {
    let cat = EMPTY_CATALOGUE;

    // Authoritative unfiltered base of 50 (ids 0..49).
    cat = mergeDemographicOptions(cat, reply(0), { unfilteredForDimension: true, selectedId: null });

    // Select a NON-base group, then flood with two more bounded scoped replies of fresh names.
    cat = mergeDemographicOptions(cat, [{ id: 1000, name: 'Picked' }], { unfilteredForDimension: false, selectedId: 1000 });
    cat = mergeDemographicOptions(cat, reply(500, 50, 'New'), { unfilteredForDimension: false, selectedId: 1000 });
    cat = mergeDemographicOptions(cat, reply(700, 50, 'Extra'), { unfilteredForDimension: false, selectedId: 1000 });

    expect(cat.options.length).toBeLessThanOrEqual(DEMOGRAPHIC_OPTION_CAP);
    // Every unfiltered-base id survived eviction...
    for (let i = 0; i < 50; i++) expect(cat.options.some((o) => o.id === i)).toBe(true);
    // ...and so did the current selection, so the user can still switch away from it.
    expect(cat.options.some((o) => o.id === 1000)).toBe(true);
    expect(cat.truncated).toBe(true);
  });

  it('pins a selected id of 0 (unknown/first group), never treating it as "no selection"', () => {
    let cat = EMPTY_CATALOGUE;
    // An "unknown" group whose id is the falsy 0, kept as the current selection...
    cat = mergeDemographicOptions(cat, [{ id: 0, name: 'Unknown' }], { unfilteredForDimension: false, selectedId: 0 });
    expect(cat.options.some((o) => o.id === 0)).toBe(true);
    // ...survives eviction while >100 fresh groups arrive across bounded replies (a `if (selectedId)`
    // falsy-check bug would drop id 0 here).
    cat = mergeDemographicOptions(cat, reply(100), { unfilteredForDimension: false, selectedId: 0 });
    cat = mergeDemographicOptions(cat, reply(200), { unfilteredForDimension: false, selectedId: 0 });
    cat = mergeDemographicOptions(cat, reply(300), { unfilteredForDimension: false, selectedId: 0 });
    expect(cat.options.length).toBeLessThanOrEqual(DEMOGRAPHIC_OPTION_CAP);
    expect(cat.options.some((o) => o.id === 0)).toBe(true);
  });

  it('replaces the retained base when a NEW unfiltered reply arrives, and does not mutate its inputs', () => {
    const prev: DemographicCatalogue = { options: [{ id: 1, name: 'Old' }], baseIds: [1], truncated: false };
    const frozenNext: DemographicOption[] = [{ id: 2, name: 'New' }];

    const cat = mergeDemographicOptions(prev, frozenNext, { unfilteredForDimension: true, selectedId: null });

    // New unfiltered base is now id 2; id 1 remains (under cap) but is no longer part of the base.
    expect(cat.baseIds).toEqual([2]);
    expect(new Set(ids(cat))).toEqual(new Set([1, 2]));

    // Inputs untouched.
    expect(prev.options).toEqual([{ id: 1, name: 'Old' }]);
    expect(prev.baseIds).toEqual([1]);
    expect(frozenNext).toEqual([{ id: 2, name: 'New' }]);
  });
});
