import { MessageBar, MessageBarBody, MessageBarActions, Button } from '@fluentui/react-components';
import { ArrowClockwise16Regular } from '@fluentui/react-icons';
import { LicenceActivityApiError, type LicenceActivityErrorKind } from '../../api/licenceActivityApi';

/** A displayable message plus the failure kind, from any thrown value. */
export function describeError(err: unknown, fallback: string): { message: string; kind?: LicenceActivityErrorKind } {
  if (err instanceof LicenceActivityApiError) return { message: err.message, kind: err.kind };
  if (err instanceof Error && err.message) return { message: err.message };
  return { message: fallback };
}

/** MessageBar intent for a failure kind: the transient / refreshable ones are warnings, the rest errors. */
function intentForKind(kind: LicenceActivityErrorKind | undefined): 'warning' | 'error' {
  return kind === 'busy' || kind === 'expired' ? 'warning' : 'error';
}

interface ApiErrorBarProps {
  error: unknown;
  fallback: string;
  /** When given, renders a retry/refresh button. The caller decides what retrying does. */
  onRetry?: () => void;
  retryLabel?: string;
}

/**
 * Renders an API failure with the right severity and, optionally, a retry affordance.
 *
 * This tool does not poll: a busy (503) result is a dead end until the user asks again, so the retry
 * button is how they do that. An expired (410/409) snapshot is offered a "Refresh" instead, because
 * the fix is to remint the snapshot, not to repeat the same doomed request.
 */
export default function ApiErrorBar({ error, fallback, onRetry, retryLabel }: ApiErrorBarProps) {
  const { message, kind } = describeError(error, fallback);
  const label = retryLabel ?? (kind === 'expired' ? 'Refresh' : 'Try again');

  return (
    <MessageBar intent={intentForKind(kind)}>
      <MessageBarBody>{message}</MessageBarBody>
      {onRetry && (
        <MessageBarActions>
          <Button size="small" icon={<ArrowClockwise16Regular />} onClick={onRetry}>
            {label}
          </Button>
        </MessageBarActions>
      )}
    </MessageBar>
  );
}
