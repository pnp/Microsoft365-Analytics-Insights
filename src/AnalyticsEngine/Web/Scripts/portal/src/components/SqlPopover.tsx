import {
  Button,
  Popover,
  PopoverTrigger,
  PopoverSurface,
  Text,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { Code16Regular, Copy16Regular } from '@fluentui/react-icons';
import toast from './toast';
import { useT } from '../i18n';

const useStyles = makeStyles({
  surface: {
    maxWidth: '560px',
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
  },
  sqlBlock: {
    fontFamily: 'Consolas, Menlo, Monaco, "Courier New", monospace',
    fontSize: tokens.fontSizeBase200,
    whiteSpace: 'pre-wrap',
    wordBreak: 'break-word',
    userSelect: 'text',
    margin: 0,
    padding: '8px',
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground3,
  },
});

type SqlPopoverProps = {
  /** The SQL to show and copy. */
  sql: string;
  /** Heading shown above the SQL block. */
  title?: string;
  /** Trigger button label. */
  buttonLabel?: string;
};

/**
 * A small "SQL" button that opens a popover showing a query with a copy-to-clipboard action, so
 * admins can reproduce a figure themselves. Mirrors the popover used by the User Lookup page.
 */
export default function SqlPopover({ sql, title, buttonLabel }: SqlPopoverProps) {
  const styles = useStyles();
  const t = useT();

  const copySql = async () => {
    try {
      await navigator.clipboard.writeText(sql);
      toast.success(t('common.sql.copied'));
    } catch {
      toast.error(t('common.sql.copyFailed'));
    }
  };

  return (
    <Popover withArrow trapFocus>
      <PopoverTrigger disableButtonEnhancement>
        <Button appearance="subtle" size="small" icon={<Code16Regular />}>
          {buttonLabel ?? t('common.sql.buttonLabel')}
        </Button>
      </PopoverTrigger>
      <PopoverSurface>
        <div className={styles.surface}>
          <Text size={200} weight="semibold">
            {title ?? t('common.sql.title')}
          </Text>
          <pre className={styles.sqlBlock}>{sql}</pre>
          <div>
            <Button appearance="primary" size="small" icon={<Copy16Regular />} onClick={copySql}>
              {t('common.action.copyToClipboard')}
            </Button>
          </div>
        </div>
      </PopoverSurface>
    </Popover>
  );
}
