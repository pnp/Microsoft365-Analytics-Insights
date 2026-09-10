// The department/country FILTER option catalogue for the Licence activity report.
//
// The backend deliberately returns only the demographic groups WITHIN the current scope (filtering by
// department X returns just X), so the drop-downs can't simply mirror the latest reply or they'd
// collapse to the selected group and strand the user unable to switch straight to another. We instead
// keep a running, merged catalogue of every group seen - but BOUNDED, so a long session of scoped
// filters can't grow it without limit. Names only: a per-scope count would be misleading across a
// cross-scope catalogue, so counts live in the demographic breakdown, never in these labels.

/** A single filter option: id + display name only (no scope-specific count). */
export interface DemographicOption {
  id: number;
  name: string;
}

/**
 * The catalogue for one dimension (departments OR countries):
 *  - `options`  the capped, name-sorted list rendered in the drop-down;
 *  - `baseIds`  the ids from the latest UNFILTERED reply for this dimension - the authoritative
 *               in-scope list (backend-bounded to <=50), always retained across later scoped replies;
 *  - `truncated` whether some known name has had to be evicted (sticky), so the UI can show an honest
 *               "options limited" note rather than silently hiding groups.
 */
export interface DemographicCatalogue {
  options: DemographicOption[];
  baseIds: number[];
  truncated: boolean;
}

export const EMPTY_CATALOGUE: DemographicCatalogue = { options: [], baseIds: [], truncated: false };

/** The hard cap on retained option NAMES per dimension. Each backend reply is itself <=50, so this
 *  bounds a whole session of scoped filters to a scannable, responsive drop-down at 50-SKU scale. */
export const DEMOGRAPHIC_OPTION_CAP = 100;

export interface MergeDemographicOptions {
  /** True when this reply's OWN filter for the dimension is null, i.e. it is the authoritative
   *  in-scope catalogue for that dimension (its ids become the retained base). */
  unfilteredForDimension: boolean;
  /** The id currently selected for this dimension (null = "All"); always pinned so it can't be
   *  evicted, guaranteeing the user can switch away from their own selection. */
  selectedId: number | null;
  cap?: number;
}

/**
 * Merge an incoming (bounded, <=50) demographic reply into the running catalogue, capped per
 * dimension. Retention priority when at capacity, highest first:
 *   1. the latest UNFILTERED catalogue (`baseIds`), so the real in-scope list always survives;
 *   2. the current `selectedId`;
 *   3. the current scoped incoming names (`next`, freshest);
 *   4. older, previously-retained names, only within remaining capacity (oldest evicted first).
 *
 * Pure and non-mutating. `truncated` is sticky: once any name is evicted it stays set, because the
 * catalogue is then known not to be exhaustive.
 */
export function mergeDemographicOptions(
  prev: DemographicCatalogue,
  next: readonly DemographicOption[],
  opts: MergeDemographicOptions,
): DemographicCatalogue {
  const cap = opts.cap ?? DEMOGRAPHIC_OPTION_CAP;

  // De-duplicate by id in freshest-first order: incoming scoped names first (newest name wins), then
  // the previously-retained names.
  const byId = new Map<number, DemographicOption>();
  const order: number[] = [];
  const consider = (o: DemographicOption): void => {
    if (byId.has(o.id)) return; // first occurrence wins, and `next` is considered first, so the freshest name is kept
    order.push(o.id);
    byId.set(o.id, { id: o.id, name: o.name });
  };
  for (const d of next) consider({ id: d.id, name: d.name });
  for (const o of prev.options) consider(o);

  // The authoritative unfiltered catalogue: replaced when a new unfiltered reply arrives, else carried.
  const baseIds = opts.unfilteredForDimension ? next.map((d) => d.id) : prev.baseIds;

  // Everything that must survive eviction.
  const pinned = new Set<number>();
  for (const id of baseIds) if (byId.has(id)) pinned.add(id);
  if (opts.selectedId != null && byId.has(opts.selectedId)) pinned.add(opts.selectedId);

  // Keep all pins (in candidate order), then fill the remaining capacity with the freshest non-pins.
  const kept: number[] = [];
  for (const id of order) if (pinned.has(id)) kept.push(id);
  for (const id of order) {
    if (kept.length >= cap) break;
    if (!pinned.has(id)) kept.push(id);
  }

  const evicted = kept.length < order.length;
  const keptSet = new Set(kept);
  const options = kept.map((id) => byId.get(id)!).sort((a, b) => a.name.localeCompare(b.name));

  return {
    options,
    baseIds: baseIds.filter((id) => keptSet.has(id)),
    truncated: prev.truncated || evicted,
  };
}
