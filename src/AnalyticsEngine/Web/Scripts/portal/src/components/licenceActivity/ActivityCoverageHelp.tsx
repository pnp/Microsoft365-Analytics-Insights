import { makeStyles, tokens, Text } from '@fluentui/react-components';
import { useT } from '../../i18n';
import { BAND_DESCRIPTION_KEYS, COPILOT_COVERAGE_NOTE_KEY } from './bands';

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: '4px',
    color: tokens.colorNeutralForeground2,
  },
  summary: {
    cursor: 'pointer',
    fontSize: tokens.fontSizeBase200,
  },
  detail: {
    display: 'flex',
    flexDirection: 'column',
    gap: '6px',
    paddingTop: '6px',
  },
});

export default function ActivityCoverageHelp({ showCopilot = false }: { showCopilot?: boolean }) {
  const styles = useStyles();
  const t = useT();

  return (
    <div className={styles.root}>
      <Text size={200} role="note">
        {t('licenceActivity.activityCoverage.summary')}
      </Text>
      <details>
        <summary className={styles.summary}>{t('licenceActivity.activityCoverage.whyUnknown')}</summary>
        <div className={styles.detail}>
          <Text size={200}>{t(BAND_DESCRIPTION_KEYS.unknown)}</Text>
          <Text size={200}>{t('licenceActivity.activityCoverage.recordedEventsOnly')}</Text>
          {showCopilot && <Text size={200}>{t(COPILOT_COVERAGE_NOTE_KEY)}</Text>}
          <Text size={200}>{t('licenceActivity.activityCoverage.showSourcesHelp')}</Text>
        </div>
      </details>
    </div>
  );
}
