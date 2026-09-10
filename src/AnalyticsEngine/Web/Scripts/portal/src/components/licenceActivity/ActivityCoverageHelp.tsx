import { makeStyles, tokens, Text } from '@fluentui/react-components';
import { BAND_DESCRIPTIONS, COPILOT_COVERAGE_NOTE } from './bands';

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

  return (
    <div className={styles.root}>
      <Text size={200} role="note">
        Unknown means insufficient data, not no activity. No activity means complete reporting data shows no usage.
      </Text>
      <details>
        <summary className={styles.summary}>Why is activity Unknown?</summary>
        <div className={styles.detail}>
          <Text size={200}>{BAND_DESCRIPTIONS.unknown}</Text>
          <Text size={200}>
            When the source contains only recorded events, finding no events does not prove that the person was inactive.
          </Text>
          {showCopilot && <Text size={200}>{COPILOT_COVERAGE_NOTE}</Text>}
          <Text size={200}>
            Under Where these figures come from, select Show data sources for the source, reporting dates and import
            status for each service.
          </Text>
        </div>
      </details>
    </div>
  );
}
