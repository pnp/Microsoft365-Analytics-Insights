import { makeStyles, tokens, Text } from '@fluentui/react-components';
import { useT } from '../../i18n';
import InfoTip from '../shared/InfoTip';
import { DASH, formatScore } from './format';

const useStyles = makeStyles({
  root: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
  },
  value: {
    minWidth: '34px',
    textAlign: 'right',
    fontVariantNumeric: 'tabular-nums',
  },
  track: {
    position: 'relative',
    width: '64px',
    height: '8px',
    borderRadius: tokens.borderRadiusSmall,
    backgroundColor: tokens.colorNeutralBackground3,
    flexShrink: 0,
  },
  fill: {
    position: 'absolute',
    insetBlockStart: 0,
    insetInlineStart: 0,
    height: '100%',
    borderRadius: tokens.borderRadiusSmall,
    backgroundColor: tokens.colorBrandBackground,
  },
  marker: {
    position: 'absolute',
    insetBlockStart: '-3px',
    width: '2px',
    height: '14px',
    backgroundColor: tokens.colorNeutralForeground1,
  },
  muted: {
    color: tokens.colorNeutralForeground3,
  },
});

/** The "i" explaining the adoption score, with the calculation itself. */
export function AdoptionScoreInfo() {
  const t = useT();
  return (
    <InfoTip
      title={t('licenceActivity.compare.adoptionScore')}
      content={{
        what: t('licenceActivity.compare.adoptionScoreWhat'),
        how: t('licenceActivity.compare.adoptionScoreHow'),
        formula: t('licenceActivity.compare.adoptionScoreFormula'),
      }}
    />
  );
}

/**
 * An adoption score as a figure and a 0-100 bar. Deliberately one neutral colour: the score is a
 * measure of how much a licence's holders use their services, not a verdict on the licence, so it is
 * never painted red or green.
 *
 * `baseline` draws a thin marker at another score - everyone holding a licence - so a licence can be
 * read against it at a glance.
 */
export default function AdoptionScoreBar({
  score,
  baseline,
  large,
}: {
  score: number | null;
  baseline?: number | null;
  large?: boolean;
}) {
  const styles = useStyles();
  const t = useT();

  if (score == null) {
    return (
      <Text size={large ? 500 : 200} className={styles.muted} title={t('licenceActivity.common.notMeasured')}>
        {DASH}
      </Text>
    );
  }

  const clamp = (v: number) => Math.max(0, Math.min(100, v));
  const label =
    baseline == null
      ? t('licenceActivity.compare.scoreAria', { score: formatScore(score) })
      : t('licenceActivity.compare.scoreWithBaselineAria', { score: formatScore(score), baseline: formatScore(baseline) });

  return (
    <span className={styles.root} role="img" aria-label={label} title={label}>
      <Text size={large ? 600 : 200} weight={large ? 'bold' : 'semibold'} className={styles.value}>
        {formatScore(score)}
      </Text>
      <span className={styles.track} style={large ? { width: '120px', height: '10px' } : undefined}>
        <span className={styles.fill} style={{ width: `${clamp(score)}%` }} />
        {baseline != null && <span className={styles.marker} style={{ insetInlineStart: `calc(${clamp(baseline)}% - 1px)` }} />}
      </span>
    </span>
  );
}
