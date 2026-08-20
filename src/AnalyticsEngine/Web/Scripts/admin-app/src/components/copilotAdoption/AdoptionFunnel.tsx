import { makeStyles, tokens, Text } from '@fluentui/react-components';
import type { ReportCategory } from '../../types/reports';
import { formatCount, formatPct } from './KpiGrid';

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
    paddingTop: '4px',
  },
  stage: {
    display: 'grid',
    gridTemplateColumns: 'minmax(120px, 170px) 1fr auto',
    alignItems: 'center',
    gap: '12px',
  },
  label: {
    color: tokens.colorNeutralForeground2,
  },
  track: {
    position: 'relative',
    height: '26px',
    borderRadius: tokens.borderRadiusSmall,
    backgroundColor: tokens.colorNeutralBackground3,
    overflow: 'hidden',
  },
  bar: {
    height: '100%',
    borderRadius: tokens.borderRadiusSmall,
    minWidth: '2px',
    transition: 'width 200ms ease',
  },
  barLabel: {
    position: 'absolute',
    insetBlockStart: 0,
    insetInlineStart: '10px',
    height: '26px',
    display: 'flex',
    alignItems: 'center',
    color: tokens.colorNeutralForegroundOnBrand,
    fontVariantNumeric: 'tabular-nums',
  },
  dropOff: {
    textAlign: 'right',
    minWidth: '120px',
    fontVariantNumeric: 'tabular-nums',
  },
  loss: {
    color: tokens.colorPaletteRedForeground1,
  },
  baseline: {
    color: tokens.colorNeutralForeground3,
  },
  empty: {
    color: tokens.colorNeutralForeground3,
    padding: '24px 0',
    textAlign: 'center',
  },
});

/**
 * Progressively darker blues down the funnel, so the shape reads as a narrowing pipeline rather
 * than as five unrelated bars.
 */
const STAGE_COLOURS = ['#a3c8ea', '#7cb0e0', '#4f93d3', '#2b7cc4', '#0f6cbd'];

/**
 * The adoption funnel: licensed -> ever used -> active -> habitual -> champion.
 *
 * Each stage is drawn as a share of the first and, the part that actually drives decisions, the
 * drop-off from the previous stage is spelled out next to it. A single "38% adoption" figure tells
 * an executive that something is wrong; this tells them *where*, which is the difference between
 * commissioning a training programme and reclaiming seats.
 */
export default function AdoptionFunnel({ stages }: { stages: ReportCategory[] }) {
  const styles = useStyles();

  if (stages.length === 0) {
    return <div className={styles.empty}>No licensed users to chart.</div>;
  }

  const top = Math.max(stages[0]?.value ?? 0, 1);

  return (
    <div className={styles.root}>
      {stages.map((stage, index) => {
        const previous = index === 0 ? null : stages[index - 1].value;
        const lost = previous === null ? 0 : previous - stage.value;
        const conversion = previous === null || previous === 0 ? 100 : (stage.value / previous) * 100;
        const widthPct = Math.max(1.5, (stage.value / top) * 100);

        return (
          <div className={styles.stage} key={stage.label}>
            <Text size={200} className={styles.label}>
              {stage.label}
            </Text>
            <div className={styles.track}>
              <div
                className={styles.bar}
                style={{ width: `${widthPct}%`, backgroundColor: STAGE_COLOURS[index % STAGE_COLOURS.length] }}
              />
              <Text size={200} weight="semibold" className={styles.barLabel}>
                {formatCount(stage.value)}
              </Text>
            </div>
            <Text size={200} className={styles.dropOff}>
              {previous === null ? (
                <span className={styles.baseline}>baseline</span>
              ) : (
                <>
                  {formatPct(conversion)}
                  {lost > 0 && <span className={styles.loss}> ({formatCount(lost)} lost)</span>}
                </>
              )}
            </Text>
          </div>
        );
      })}
    </div>
  );
}
