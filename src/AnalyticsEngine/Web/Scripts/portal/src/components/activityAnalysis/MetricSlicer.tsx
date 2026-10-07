import { useId, useState } from 'react';
import { Button, Card, Checkbox, Text, makeStyles, tokens } from '@fluentui/react-components';
import { ChevronDown16Regular, ChevronRight16Regular } from '@fluentui/react-icons';
import { formatNumber, plural, useT } from '../../i18n';
import {
  categoryCheckState,
  categoryLabel,
  metricLabel,
  toggleCategory,
  toggleMetric,
  type MetricGroup,
} from './metrics';

const useStyles = makeStyles({
  card: {
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
    padding: '12px 12px 14px',
  },
  head: {
    display: 'flex',
    alignItems: 'baseline',
    justifyContent: 'space-between',
    gap: '8px',
    flexWrap: 'wrap',
  },
  actions: {
    display: 'flex',
    gap: '4px',
    flexWrap: 'wrap',
    marginLeft: '-6px',
  },
  tree: {
    listStyleType: 'none',
    margin: 0,
    padding: 0,
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
  },
  categoryRow: {
    display: 'flex',
    alignItems: 'center',
    gap: '2px',
    borderRadius: tokens.borderRadiusMedium,
    ':hover': {
      backgroundColor: tokens.colorNeutralBackground1Hover,
    },
  },
  expander: {
    minWidth: '24px',
    width: '24px',
    height: '24px',
    padding: 0,
    flexShrink: 0,
  },
  categoryCheckbox: {
    flexGrow: 1,
    minWidth: 0,
    fontWeight: tokens.fontWeightSemibold,
  },
  count: {
    color: tokens.colorNeutralForeground3,
    fontVariantNumeric: 'tabular-nums',
    whiteSpace: 'nowrap',
    paddingRight: '4px',
  },
  metrics: {
    listStyleType: 'none',
    margin: '0 0 6px 0',
    padding: '0 0 0 26px',
    // One column in the side rail; several when the slicer runs the full width of a narrow page.
    columnWidth: '230px',
    columnGap: '16px',
    '&>li': {
      breakInside: 'avoid',
    },
  },
  muted: {
    color: tokens.colorNeutralForeground3,
  },
});

interface MetricSlicerProps {
  groups: MetricGroup[];
  /** Every available metric key in slicer order - the order the selection is kept in. */
  order: string[];
  selected: string[];
  onChange: (next: string[]) => void;
  /** Metrics the server knows but this installation's profiling tables do not have. */
  unavailableCount: number;
}

/**
 * The metric slicer: the Power BI tree of categories and metrics, as nested checkbox lists.
 *
 * A category's checkbox is tri-state - ticked, empty, or mixed when only some of its metrics are
 * chosen - and its metrics fold away under a disclosure button, so the 58 metrics do not have to be on
 * screen at once. Every control is a native button or checkbox, so it works from the keyboard with Tab
 * and Space, and announces its state, without a custom tree widget's arrow-key contract.
 */
export default function MetricSlicer({ groups, order, selected, onChange, unavailableCount }: MetricSlicerProps) {
  const styles = useStyles();
  const t = useT();
  const titleId = useId();
  const listIdPrefix = useId();

  // Opens on the categories the selection is in, so the reader can see what the figures show.
  const [expanded, setExpanded] = useState<ReadonlySet<string>>(
    () => new Set(groups.filter((g) => g.metrics.some((m) => selected.includes(m.key))).map((g) => g.category)),
  );

  const toggleExpanded = (category: string) =>
    setExpanded((current) => {
      const next = new Set(current);
      if (next.has(category)) next.delete(category);
      else next.add(category);
      return next;
    });

  return (
    <Card className={styles.card} data-print="hide">
      <div className={styles.head}>
        <Text id={titleId} weight="semibold" size={400}>
          {t('activityAnalysis.slicer.title')}
        </Text>
        <Text size={200} className={styles.muted} aria-live="polite">
          {t(plural(selected.length, 'activityAnalysis.slicer.selected.one', 'activityAnalysis.slicer.selected.other'), {
            count: formatNumber(selected.length),
          })}
        </Text>
      </div>

      <div className={styles.actions}>
        <Button size="small" appearance="subtle" disabled={selected.length === order.length} onClick={() => onChange([...order])}>
          {t('activityAnalysis.slicer.selectAll')}
        </Button>
        <Button size="small" appearance="subtle" disabled={selected.length === 0} onClick={() => onChange([])}>
          {t('activityAnalysis.slicer.clear')}
        </Button>
      </div>

      <ul className={styles.tree} aria-labelledby={titleId}>
        {groups.map((group) => {
          const open = expanded.has(group.category);
          const label = categoryLabel(t, group.category);
          const listId = `${listIdPrefix}-${group.category}`;
          const chosen = group.metrics.filter((m) => selected.includes(m.key)).length;

          return (
            <li key={group.category}>
              <div className={styles.categoryRow}>
                <Button
                  appearance="transparent"
                  size="small"
                  className={styles.expander}
                  icon={open ? <ChevronDown16Regular /> : <ChevronRight16Regular />}
                  aria-expanded={open}
                  aria-controls={listId}
                  aria-label={
                    open
                      ? t('activityAnalysis.slicer.collapse', { category: label })
                      : t('activityAnalysis.slicer.expand', { category: label })
                  }
                  onClick={() => toggleExpanded(group.category)}
                />
                <Checkbox
                  className={styles.categoryCheckbox}
                  checked={categoryCheckState(selected, group)}
                  onChange={() => onChange(toggleCategory(selected, group, order))}
                  label={label}
                />
                <Text size={200} className={styles.count}>
                  {t('activityAnalysis.slicer.count', {
                    selected: formatNumber(chosen),
                    total: formatNumber(group.metrics.length),
                  })}
                </Text>
              </div>

              {open && (
                <ul id={listId} className={styles.metrics} aria-label={label}>
                  {group.metrics.map((metric) => (
                    <li key={metric.key}>
                      <Checkbox
                        checked={selected.includes(metric.key)}
                        onChange={() => onChange(toggleMetric(selected, metric.key, order))}
                        label={metricLabel(t, metric.key, metric.label)}
                      />
                    </li>
                  ))}
                </ul>
              )}
            </li>
          );
        })}
      </ul>

      {unavailableCount > 0 && (
        <Text size={200} className={styles.muted}>
          {t(plural(unavailableCount, 'activityAnalysis.slicer.unavailable.one', 'activityAnalysis.slicer.unavailable.other'), {
            count: formatNumber(unavailableCount),
          })}
        </Text>
      )}
    </Card>
  );
}
