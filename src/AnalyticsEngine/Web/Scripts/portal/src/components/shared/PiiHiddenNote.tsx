import { Card, Text, makeStyles, tokens } from '@fluentui/react-components';
import { useT } from '../../i18n';
import { usePortalAccess } from '../../access';

const useStyles = makeStyles({
  note: {
    display: 'flex',
    flexDirection: 'column',
    gap: '4px',
  },
  muted: {
    color: tokens.colorNeutralForeground3,
  },
});

/**
 * What a reader without the See PII permission sees in place of anything about an individual person:
 * a statement that it is hidden and which app role would show it - never an empty table, which would
 * read as "there is no data".
 */
export default function PiiHiddenNote() {
  const t = useT();
  const styles = useStyles();
  const { roles } = usePortalAccess();
  return (
    <Card className={styles.note} role="note">
      <Text weight="semibold">{t('access.piiHidden.title')}</Text>
      <Text size={200} className={styles.muted}>{t('access.piiHidden.message', { role: roles.seePii })}</Text>
    </Card>
  );
}
