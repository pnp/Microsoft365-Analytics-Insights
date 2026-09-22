import { Button, Spinner, Text, makeStyles, tokens } from '@fluentui/react-components';
import { useT } from '../../i18n';

const useStyles = makeStyles({
  root: {
    display: 'flex',
    alignItems: 'center',
    gap: '16px',
    flexWrap: 'wrap',
    paddingBlock: '8px',
  },
  summary: {
    color: tokens.colorNeutralForeground2,
  },
});

type ConfirmSelectionProps = {
  saveCallback: () => void;
  authCount: number;
  deAuthCount: number;
  /** When false a spinner is shown instead of the save button (matches the caller's inverted flag). */
  isBusy: boolean;
};

export default function ConfirmSelection(props: ConfirmSelectionProps) {
  const styles = useStyles();
  const t = useT();
  return (
    <div className={styles.root}>
      <Text weight="semibold">{t('admin.teams.confirmSelection.actionsToApply')}</Text>
      <Text className={styles.summary}>
        {t('admin.teams.confirmSelection.summary', { deAuthCount: props.deAuthCount, authCount: props.authCount })}
      </Text>
      {!props.isBusy ? (
        <Spinner size="tiny" />
      ) : (
        <Button appearance="primary" onClick={() => props.saveCallback()}>
          {t('admin.teams.confirmSelection.saveChanges')}
        </Button>
      )}
    </div>
  );
}
