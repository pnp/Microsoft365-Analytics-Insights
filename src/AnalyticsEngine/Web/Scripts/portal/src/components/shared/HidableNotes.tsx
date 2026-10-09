import { useState, type CSSProperties } from 'react';
import {
  makeStyles,
  Button,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  MessageBarTitle,
} from '@fluentui/react-components';
import { Dismiss16Regular, Info16Regular } from '@fluentui/react-icons';
import { plural, useT } from '../../i18n';

const useStyles = makeStyles({
  list: {
    margin: '4px 0 0 0',
    paddingInlineStart: '20px',
  },
});

const STORAGE_PREFIX = 'portal.notesHidden.';

function readHidden(storageKey: string): boolean {
  try {
    return window.localStorage.getItem(STORAGE_PREFIX + storageKey) === '1';
  } catch {
    // Storage can be unavailable (privacy mode, a locked-down browser): the notes simply show.
    return false;
  }
}

function writeHidden(storageKey: string, hidden: boolean): void {
  try {
    if (hidden) window.localStorage.setItem(STORAGE_PREFIX + storageKey, '1');
    else window.localStorage.removeItem(STORAGE_PREFIX + storageKey);
  } catch {
    // See readHidden.
  }
}

/**
 * The explanatory notes above a report - how the figures are worked out and what they cannot tell
 * you - in a panel the reader can put away.
 *
 * Unlike `DismissibleWarnings`, these are not problems with the data: they are the same few sentences
 * on every visit, so once a reader has read and hidden them they stay hidden (remembered per report in
 * this browser) until the reader asks for them back. Hidden, they collapse to a button that still says
 * how many notes there are, so nobody can be unaware they exist - and, as with the warnings, hiding them
 * also leaves them off a printout.
 */
export default function HidableNotes({
  notes,
  storageKey,
  style,
}: {
  /** The notes, already in the reader's language. */
  notes: string[];
  /** Remembers the reader's choice for this report, e.g. `licenceActivity`. */
  storageKey: string;
  style?: CSSProperties;
}) {
  const styles = useStyles();
  const t = useT();
  const [hidden, setHidden] = useState(() => readHidden(storageKey));

  if (notes.length === 0) return null;

  const toggle = (next: boolean) => {
    setHidden(next);
    writeHidden(storageKey, next);
  };

  if (hidden) {
    return (
      <div style={style}>
        <Button
          appearance="subtle"
          size="small"
          icon={<Info16Regular />}
          data-print="hide"
          aria-expanded={false}
          onClick={() => toggle(false)}
        >
          {t(plural(notes.length, 'common.notes.show.one', 'common.notes.show.other'), { count: notes.length })}
        </Button>
      </div>
    );
  }

  return (
    <MessageBar intent="info" layout="multiline" style={style}>
      <MessageBarBody>
        <MessageBarTitle>{t('common.notes.title')}</MessageBarTitle>
        <ul className={styles.list}>
          {notes.map((note) => (
            <li key={note}>{note}</li>
          ))}
        </ul>
      </MessageBarBody>
      <MessageBarActions
        containerAction={
          <Button
            appearance="transparent"
            icon={<Dismiss16Regular />}
            aria-label={t('common.notes.hide')}
            title={t('common.notes.hide')}
            aria-expanded={true}
            data-print="hide"
            onClick={() => toggle(true)}
          />
        }
      />
    </MessageBar>
  );
}
