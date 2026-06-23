import Spinner from '../Spinner';

type ConfirmSelectionProps = {
  saveCallback: () => void;
  authCount: number;
  deAuthCount: number;
  /** When false a spinner is shown instead of the save button (matches the caller's inverted flag). */
  isBusy: boolean;
};

export default function ConfirmSelection(props: ConfirmSelectionProps) {
  return (
    <div>
      <div style={{ width: '100%', overflow: 'hidden' }}>
        <div style={{ float: 'left', fontWeight: 600 }}>Actions to apply:</div>
        <div style={{ marginLeft: '180px' }}>
          <div>
            De-authorise {props.deAuthCount} Team(s); Authorise {props.authCount} Team(s)
          </div>
        </div>
      </div>
      {!props.isBusy ? (
        <Spinner size={30} />
      ) : (
        <button type="button" className="btn btn-primary" onClick={() => props.saveCallback()}>
          Save Changes
        </button>
      )}
    </div>
  );
}
