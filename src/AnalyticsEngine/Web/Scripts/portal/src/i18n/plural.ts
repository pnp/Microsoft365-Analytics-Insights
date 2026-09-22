import type { TranslationKey } from './catalog';

/**
 * Picks the singular or plural key for a count.
 *
 * Deliberately not a pluralisation engine. English and Spanish share the same two CLDR plural
 * categories (`one` for exactly 1, `other` for everything else), so a two-key choice is correct
 * for both languages and, unlike a runtime rules engine, keeps both forms of every phrase as real
 * catalog keys - which means the compiler still proves Spanish has them, and a translator can see
 * both forms side by side.
 *
 * A language with more categories (Polish, Arabic, Russian) would need a real plural selector.
 * That is a deliberate deferral, noted here so the next person does not mistake this for an
 * oversight.
 *
 * ```ts
 * t(plural(n, 'common.unit.user.one', 'common.unit.user.other'), { count: n });
 * ```
 */
export function plural<K extends TranslationKey>(count: number, one: K, other: K): K {
  return count === 1 ? one : other;
}
