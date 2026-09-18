import type { ReactElement } from 'react';
import { Text, makeStyles, tokens } from '@fluentui/react-components';
import {
  ChartMultiple20Regular,
  DataUsage20Regular,
  Money20Regular,
  PeopleTeam20Regular,
  Pulse20Regular,
  Settings20Regular,
  ShieldProhibited20Regular,
  Sparkle20Regular,
} from '@fluentui/react-icons';

const useStyles = makeStyles({
  grid: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fill, minmax(260px, 1fr))',
    gap: '12px',
  },
  /**
   * A card-looking anchor rather than a Fluent `Card`: the whole tile has to be one link, and Card's
   * root slot is typed as a `div`, so it cannot carry an href. Styled to match the Card elsewhere on
   * the page so the two rows of tiles read as one design.
   */
  tile: {
    display: 'flex',
    flexDirection: 'column',
    gap: '4px',
    padding: '14px 16px',
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground1,
    boxShadow: tokens.shadow2,
    textDecoration: 'none',
    color: 'inherit',
    ':hover': {
      backgroundColor: tokens.colorNeutralBackground1Hover,
      boxShadow: tokens.shadow8,
    },
    ':focus-visible': {
      outline: `2px solid ${tokens.colorStrokeFocus2}`,
    },
  },
  head: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
    color: tokens.colorBrandForeground1,
  },
  title: {
    color: tokens.colorNeutralForeground1,
  },
  blurb: {
    color: tokens.colorNeutralForeground3,
  },
});

interface Pointer {
  key: string;
  /** Hash route, e.g. '#/insights/reports' - the portal uses a HashRouter. */
  href: string;
  title: string;
  blurb: string;
  icon: ReactElement;
  /**
   * Which figures must be present for this pointer to be worth showing. The server only returns a
   * figure when its import is switched on, so this doubles as "is that workload configured?".
   * Omitted means "always relevant".
   */
  needsAnyOf?: string[];
}

/**
 * Every pointer in declaration order. Pages that depend on a specific import are hidden when that
 * import is off - sending an admin to a Copilot adoption screen that structurally cannot have data is
 * worse than not mentioning it. Reports, licence activity and the two Administration pages are always
 * shown: they work off whatever is imported, or are about running the service itself.
 */
const POINTERS: Pointer[] = [
  {
    key: 'reports',
    href: '#/insights/reports',
    title: 'Reports',
    blurb: 'Chart activity, Copilot use and page traffic over time, sliced by department or site.',
    icon: <ChartMultiple20Regular />,
  },
  {
    key: 'copilot-adoption',
    href: '#/insights/copilot-adoption',
    title: 'Copilot Adoption',
    blurb: 'Who is getting value from their Copilot licence, and who has stopped using it.',
    icon: <Sparkle20Regular />,
    needsAnyOf: ['copilotInteractions', 'copilotAiInteractions'],
  },
  {
    key: 'licence-activity',
    href: '#/insights/licence-activity',
    title: 'Licence activity',
    blurb: 'Licences you are paying for against the activity actually seen, service by service.',
    icon: <DataUsage20Regular />,
  },
  {
    key: 'agent-costs',
    href: '#/insights/agent-costs',
    title: 'Agent costs',
    blurb: 'Billed Copilot Studio credits and Azure spend, attributed per agent.',
    icon: <Money20Regular />,
    needsAnyOf: ['copilotStudioCreditDays', 'azureCostDays'],
  },
  {
    key: 'dlp',
    href: '#/insights/dlp',
    title: 'DLP impact',
    blurb: 'Where Purview data-loss-prevention policies are blocking people and agents.',
    icon: <ShieldProhibited20Regular />,
    needsAnyOf: ['dlpMatches'],
  },
  {
    key: 'teams-permissions',
    href: '#/admin/teams-permissions',
    title: 'Teams permissions',
    blurb: 'Turn on channel-level Teams analytics, one team at a time.',
    icon: <PeopleTeam20Regular />,
    needsAnyOf: ['teams'],
  },
  {
    key: 'health',
    href: '#/admin/health',
    title: 'Service health',
    blurb: 'Import liveness, exceptions, component health and database freshness.',
    icon: <Pulse20Regular />,
  },
  {
    key: 'configuration',
    href: '#/admin/configuration',
    title: 'Service configuration',
    blurb: 'Which imports are switched on, the database schema version and connected services.',
    icon: <Settings20Regular />,
  },
];

/** The pointers worth showing, given the figures this deployment actually returns. */
export function visiblePointers(availableKeys: readonly string[]): Pointer[] {
  const available = new Set(availableKeys);
  return POINTERS.filter((p) => !p.needsAnyOf || p.needsAnyOf.some((k) => available.has(k)));
}

/**
 * "What else is in here?" - a short, clickable tour of the portal for someone who has just landed on it.
 */
export default function WhereToNext({ availableKeys }: { availableKeys: readonly string[] }) {
  const styles = useStyles();
  const pointers = visiblePointers(availableKeys);

  return (
    <div className={styles.grid}>
      {pointers.map((p) => (
        <a key={p.key} href={p.href} className={styles.tile}>
          <div className={styles.head}>
            {p.icon}
            <Text weight="semibold" className={styles.title}>
              {p.title}
            </Text>
          </div>
          <Text size={200} className={styles.blurb}>
            {p.blurb}
          </Text>
        </a>
      ))}
    </div>
  );
}
