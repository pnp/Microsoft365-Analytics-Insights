import type { UserFilterClause } from './userFilter';

/**
 * The administrator's global report filter: conditions every Insights report applies for everyone who
 * opens it, on top of any filter the reader adds, and which only a portal administrator can change.
 *
 * Mirrors `Common.Entities.UserFilters.GlobalFilter*` and `Web.AnalyticsWeb.Models.UserFilters.GlobalFilterApiModels`.
 * The server enforces it on every report request; these types only describe it.
 */

/**
 * One condition: a user-filter condition whose values can also include the value the person viewing
 * the report holds for an attribute - "Department is the viewer's own department".
 */
export interface GlobalFilterClause extends UserFilterClause {
  /** The viewer's attribute whose value the condition also matches, or null for fixed values only. */
  viewerAttribute: string | null;
}

export interface GlobalFilterDefinition {
  clauses: GlobalFilterClause[];
}

export const EMPTY_GLOBAL_FILTER: GlobalFilterDefinition = { clauses: [] };

/** A condition as it applies to the signed-in reader, with their own value filled in. */
export interface GlobalFilterClauseEcho extends GlobalFilterClause {
  /** The reader's own value for `viewerAttribute`. Tenant data, shown exactly as stored. */
  viewerValue: string | null;
  /** True when the condition needed a value the reader does not have, so it matches nobody. */
  unresolved: boolean;
  /**
   * How many of the administrator's values were withheld because they are sign-in names and the reader lacks
   * See PII. Absent or zero when nothing was withheld.
   */
  hiddenValues?: number;
  /** True when `viewerValue` was withheld for the same reason: it resolved, but names a person. */
  viewerValueHidden?: boolean;
}

/** The global filter as it applies to the signed-in reader. */
export interface GlobalFilterEcho {
  clauses: GlobalFilterClauseEcho[];
  /** How many people in the directory the filter leaves this reader seeing. */
  matchedPeople: number;
  directoryPeople: number;
  /** Whether the directory holds the reader - false makes every condition on their own values match nobody. */
  viewerFound: boolean;
  /** Custom organisation types the filter names that no longer exist. A condition on one matches nobody. */
  unknownDimensions: string[];
  /** The administrator's names for the custom organisation types used, keyed by dimension. */
  dimensionNames: Record<string, string>;
}

/** `GET api/GlobalFilter/effective`: the global filter as it applies to the signed-in reader. */
export interface GlobalFilterEffective {
  /** True when a global filter is defined. */
  active: boolean;
  /** True when it narrows this reader's reports - false when none is defined or they switched it off. */
  applied: boolean;
  /** True when a portal administrator switched it off for their own view. */
  bypassed: boolean;
  /** True when this reader may switch it off for their own view, and edit it. */
  canBypass: boolean;
  revision: number;
  /** Null when none is defined or the stored filter cannot be read. */
  filter: GlobalFilterEcho | null;
  /** True when the stored filter cannot be read by this version: reports are refused until it is fixed. */
  invalid: boolean;
  /**
   * True when the filter leaves this reader, who lacks See PII, fewer than `minimumPeople` people - so their
   * reports are refused with the See PII permission message.
   */
  tooFewPeople?: boolean;
  /** The fewest people the filter may leave a reader without See PII, other than none. */
  minimumPeople?: number;
}

/** `GET api/GlobalFilter`: the definition, for the administrator's editor. */
export interface GlobalFilterAdmin {
  /** The definition in its wire form. Empty for no filter. */
  filter: string;
  /** The conditions, without anyone's own values filled in. */
  clauses: GlobalFilterClauseEcho[];
  /** Sent back with a save, so a save from a page opened before someone else's change is refused. */
  revision: number;
  modifiedUtc: string | null;
  /** The sign-in name of the administrator who last saved it. Tenant data. */
  modifiedBy: string | null;
  /** False when the database has not been upgraded to hold a global filter. */
  storageAvailable: boolean;
  /** False when portal roles are not enforced, so everyone who can sign in is an administrator. */
  rolesEnforced: boolean;
  /** True when the stored filter cannot be read by this version. */
  invalid: boolean;
  /** The fewest people the filter may leave a reader without See PII before their reports are refused. */
  minimumPeopleWithoutSeePii?: number;
}
