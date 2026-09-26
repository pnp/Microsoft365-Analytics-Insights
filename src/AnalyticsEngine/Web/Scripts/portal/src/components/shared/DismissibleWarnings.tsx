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
import { plural, useT } from '../../i18n';

const useStyles = makeStyles({
  list: {
    margin: 0,
    paddingInlineStart: '20px',
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
 * - Dismissing applies to the printout too. It briefly did not, on the reasoning that a printed
 *   report is read away from the screen by people who cannot check what it left out - but that
 *   makes the control lie: someone who puts the warnings away and prints has said what they want
 *   on the page, and getting them anyway reads as a bug. Leaving the bar up is how you print it.
 *
 * Errors are deliberately NOT routed through here. A bar saying the figures themselves are wrong
 * is not a caveat the reader gets to decide about.
 */
export default function DismissibleWarnings({
  messages,
  identities,
  style,
}: {
  messages: string[];
  identities?: string[];
  style?: CSSProperties;
}) {
  const styles = useStyles();
  const t = useT();
  const [dismissedKey, setDismissedKey] = useState<string | null>(null);

  if (messages.length === 0) return null;

  const key = (identities && identities.length === messages.length ? identities : messages).join('\u0000');

  if (dismissedKey === key) {
    return (
      <Button
        appearance="subtle"
        size="small"
        icon={<Warning16Regular />}
        style={style}
        data-print="hide"
        onClick={() => setDismissedKey(null)}
      >
        {t(plural(messages.length, 'common.warnings.show.one', 'common.warnings.show.other'), {
          count: messages.length,
        })}
      </Button>
    );
  }

  return (
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
            aria-label={t('common.warnings.hide')}
            title={t('common.warnings.hide')}
            data-print="hide"
            onClick={() => setDismissedKey(key)}
          />
        }
      />
    </MessageBar>
  );
}
