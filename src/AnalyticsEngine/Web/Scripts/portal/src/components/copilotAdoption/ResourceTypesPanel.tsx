import { makeStyles, tokens, Text } from '@fluentui/react-components';
import { CopilotResourceTypeKind } from '../../types/copilotAdoption';
import type { AdoptionResourceTypeRow } from '../../types/copilotAdoption';
import { formatValue, seriesColor, seriesColorLight } from '../charts/chartCommon';
import { serverPlaceholderText } from '../shared/serverPlaceholder';
import { useT, type TranslationKey } from '../../i18n';

/**
 * The groups, in the order they are shown, with the wording the Excel workbook uses for the same
 * buckets (Common/Entities/Copilot/CopilotAccessedResourceTaxonomy.cs).
 *
 * Tenant content first because it is the only group that answers "what content is Copilot working
 * on"; unclassified last because it is a residue rather than a finding.
 */
const GROUPS: { kind: CopilotResourceTypeKind; titleKey: TranslationKey; explanationKey: TranslationKey }[] = [
  {
    kind: CopilotResourceTypeKind.TenantContent,
    titleKey: 'copilotAdoptionAgents.resourceTypes.group.tenantContent.title',
    explanationKey: 'copilotAdoptionAgents.resourceTypes.group.tenantContent.explanation',
  },
  {
    kind: CopilotResourceTypeKind.UsageRole,
    titleKey: 'copilotAdoptionAgents.resourceTypes.group.usageRole.title',
    explanationKey: 'copilotAdoptionAgents.resourceTypes.group.usageRole.explanation',
  },
  {
    kind: CopilotResourceTypeKind.ExternalGrounding,
    titleKey: 'copilotAdoptionAgents.resourceTypes.group.externalGrounding.title',
    explanationKey: 'copilotAdoptionAgents.resourceTypes.group.externalGrounding.explanation',
  },
  {
    kind: CopilotResourceTypeKind.Unclassified,
    titleKey: 'copilotAdoptionAgents.resourceTypes.group.unclassified.title',
    explanationKey: 'copilotAdoptionAgents.resourceTypes.group.unclassified.explanation',
  },
];

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: '18px',
    width: '100%',
    paddingTop: '4px',
  },
  group: {
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
  },
  groupHead: {
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
  },
  explanation: {
    color: tokens.colorNeutralForeground3,
  },
  row: {
    display: 'grid',
    gridTemplateColumns: 'minmax(90px, 160px) 1fr auto',
    alignItems: 'center',
    gap: '12px',
  },
  label: {
    color: tokens.colorNeutralForeground2,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  track: {
    position: 'relative',
    height: '20px',
    borderRadius: '10px',
    backgroundColor: tokens.colorNeutralBackground3,
    overflow: 'hidden',
  },
  bar: {
    height: '100%',
    borderRadius: '10px',
    minWidth: '3px',
  },
  value: {
    fontVariantNumeric: 'tabular-nums',
    textAlign: 'right',
    minWidth: '48px',
  },
  empty: {
    color: tokens.colorNeutralForeground3,
    padding: '24px 0',
    textAlign: 'center',
  },
});

type ResourceTypesPanelProps = {
  rows: AdoptionResourceTypeRow[];
};

/**
 * "What Copilot referenced": the raw `AccessedResources[].Type` values from Microsoft's Copilot audit
 * log, grouped by what each value actually describes.
 *
 * This is not one chart of one taxonomy, and it deliberately does not pretend to be. Microsoft mixes
 * file kinds, Graph entity names, how the resource was used (`CITATION`) and grounding from outside
 * the tenant (`WebSearchQuery`) into that single field, so bars from different groups are not
 * comparable as "kinds of content". Grouping them is what makes that readable - the previous single
 * chart put `CITATION`, which is usually the largest bucket, next to `pdf` under a subtitle claiming
 * both were kinds of tenant content. See issue #468.
 *
 * Bars are scaled against the largest value ACROSS ALL GROUPS, not within a group, so the visual
 * lengths stay honest: a group that is a rounding error does not get redrawn as a full-width bar.
 */
export default function ResourceTypesPanel({ rows }: ResourceTypesPanelProps) {
  const styles = useStyles();
  const t = useT();

  if (rows.length === 0) {
    return <div className={styles.empty}>{t('copilotAdoptionAgents.resourceTypes.empty')}</div>;
  }

  const max = Math.max(...rows.map((r) => r.value), 1);
  const knownKinds = new Set(GROUPS.map((g) => g.kind));

  // A kind this build does not know about - a newer server adding an enum member - falls into
  // Unclassified rather than being filtered out of the chart. Dropping the row would be the same
  // silent loss the raw-Type chart was reported for.
  const rowsFor = (kind: CopilotResourceTypeKind) =>
    rows
      .filter((r) =>
        kind === CopilotResourceTypeKind.Unclassified ? !knownKinds.has(r.kind) || r.kind === kind : r.kind === kind,
      )
      .sort((a, b) => b.value - a.value);

  return (
    <div className={styles.root}>
      {GROUPS.map((group, groupIndex) => {
        const groupRows = rowsFor(group.kind);
        if (groupRows.length === 0) return null;

        return (
          <div className={styles.group} key={group.kind}>
            <div className={styles.groupHead}>
              <Text size={200} weight="semibold">
                {t(group.titleKey)}
              </Text>
              <Text size={100} className={styles.explanation}>
                {t(group.explanationKey)}
              </Text>
            </div>
            {groupRows.map((r) => {
              const pct = Math.max(1, (r.value / max) * 100);
              const label = serverPlaceholderText(t, r.label);

              return (
                <div
                  className={styles.row}
                  key={r.label}
                  title={t('copilotAdoptionAgents.resourceTypes.referenceTitle', {
                    label,
                    count: formatValue(r.value),
                  })}
                >
                  <Text size={200} className={styles.label}>
                    {label}
                  </Text>
                  <div className={styles.track}>
                    <div
                      className={styles.bar}
                      style={{
                        width: `${pct}%`,
                        backgroundImage: `linear-gradient(90deg, ${seriesColor(groupIndex)} 0%, ${seriesColorLight(groupIndex)} 100%)`,
                      }}
                    />
                  </div>
                  <Text size={200} weight="semibold" className={styles.value}>
                    {formatValue(r.value)}
                  </Text>
                </div>
              );
            })}
          </div>
        );
      })}
    </div>
  );
}
