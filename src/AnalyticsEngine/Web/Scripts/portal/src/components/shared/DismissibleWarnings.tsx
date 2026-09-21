import { useState } from 'react';
import {
  makeStyles,
  Button,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
} from '@fluentui/react-components';
import { Dismiss16Regular, Warning16Regular } from '@fluentui/react-icons';
import type { CSSProperties } from 'react';

const useStyles = makeStyles({
  list: {
    margin: 0,
    paddingInlineStart: '20px',
  },
  // Paper only - see the `data-print` contract in index.css.
  printOnly: {
    display: 'none',
  },
});

/**
 * A warning bar the reader can put away.
 *
 * These warnings are caveats about the data behind the report - an import that has not run, a
 * permission that was never granted, a figure the source cannot supply. They are worth saying, but
 * they are also the same three or four sentences on every visit to a tenant that is not going to
 * fix them today, and a permanent block of orange above the report trains people to skim past
 * everything at the top of the page, including the bars that do matter.
 *
 * So it is dismissible but not disposable:
 *
 * - Dismissing collapses it to a button that still says how many warnings there are, so the reader
 *   can always get them back and can never be unaware that they exist.
 * - The dismissal is keyed on the warnings themselves, not on the bar. A different warning - a new
 *   problem, or a different one after changing the period or the email domain - is a warning the
 *   reader has not seen, so it shows itself again rather than inheriting the old dismissal.
 * - Printing ignores the dismissal entirely. A printed report is read away from the screen by
 *   people who cannot check what it left out, so its caveats travel with it.
 */
export default function DismissibleWarnings({
  messages,
  style,
}: {
  messages: string[];
  style?: CSSProperties;
}) {
  const styles = useStyles();
  const [dismissedKey, setDismissedKey] = useState<string | null>(null);

  if (messages.length === 0) return null;

  const key = messages.join('\u0000');

  const bar = (
    <MessageBar intent="warning" style={style}>
      <MessageBarBody>
        <ul className={styles.list}>
          {messages.map((m) => (
            <li key={m}>{m}</li>
          ))}
        </ul>
      </MessageBarBody>
      <MessageBarActions
        containerAction={
          <Button
            appearance="transparent"
            icon={<Dismiss16Regular />}
            aria-label="Hide these warnings"
            title="Hide these warnings"
            data-print="hide"
            onClick={() => setDismissedKey(key)}
          />
        }
      />
    </MessageBar>
  );

  if (dismissedKey !== key) return bar;

  return (
    <>
      <Button
        appearance="subtle"
        size="small"
        icon={<Warning16Regular />}
        style={style}
        data-print="hide"
        onClick={() => setDismissedKey(null)}
      >
        {messages.length === 1 ? 'Show 1 data warning' : `Show ${messages.length} data warnings`}
      </Button>
      <div className={styles.printOnly} data-print="only">
        {bar}
      </div>
    </>
  );
}
