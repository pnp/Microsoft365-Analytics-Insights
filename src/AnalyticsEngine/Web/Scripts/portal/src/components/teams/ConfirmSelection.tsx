import { Button, Spinner, Text, makeStyles, tokens } from '@fluentui/react-components';

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
  return (
    <div className={styles.root}>
      <Text weight="semibold">Actions to apply:</Text>
      <Text className={styles.summary}>
        De-authorise {props.deAuthCount} Team(s); Authorise {props.authCount} Team(s)
      </Text>
      {!props.isBusy ? (
        <Spinner size="tiny" />
      ) : (
        <Button appearance="primary" onClick={() => props.saveCallback()}>
          Save Changes
        </Button>
      )}
    </div>
  );
}
