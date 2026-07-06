import { useEffect, useState } from 'react';
import {
  Title3,
  Body1,
  Text,
  Badge,
  Button,
  Popover,
  PopoverTrigger,
  PopoverSurface,
  Card,
  Dialog,
  DialogTrigger,
  DialogSurface,
  DialogTitle,
  DialogBody,
  DialogContent,
  DialogActions,
  Table,
  TableHeader,
  TableHeaderCell,
  TableBody,
  TableRow,
  TableCell,
  MessageBar,
  MessageBarBody,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { DocumentText16Regular, TextBulletListSquare16Regular } from '@fluentui/react-icons';
import { fetchInstallLog } from '../api/installLogApi';
import type { InstallLogEntry } from '../types/installLog';
import Spinner from '../components/Spinner';

const useStyles = makeStyles({
  configJson: {
    fontFamily: 'Consolas, Menlo, Monaco, "Courier New", monospace',
    fontSize: tokens.fontSizeBase200,
    whiteSpace: 'pre-wrap',
    wordBreak: 'break-word',
    userSelect: 'text',
    margin: 0,
    padding: '12px',
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground3,
    maxHeight: '60vh',
    overflow: 'auto',
    maxWidth: '70vw',
    minWidth: '480px',
  },
  messages: {
    whiteSpace: 'pre-wrap',
    wordBreak: 'break-word',
    color: tokens.colorNeutralForeground2,
  },
  logViewer: {
    fontFamily: 'Consolas, Menlo, Monaco, "Courier New", monospace',
    fontSize: tokens.fontSizeBase200,
    whiteSpace: 'pre-wrap',
    wordBreak: 'break-word',
    userSelect: 'text',
    margin: 0,
    padding: '12px',
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground3,
    maxHeight: '70vh',
    overflow: 'auto',
  },
  card: {
    marginTop: '16px',
  },
  dialogSurface: {
    maxWidth: '90vw',
    width: '900px',
  },
});

export default function InstallLogPage() {
  const styles = useStyles();
  const [entries, setEntries] = useState<InstallLogEntry[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    let cancelled = false;
    fetchInstallLog()
      .then((e) => {
        if (!cancelled) setEntries(e);
      })
      .catch((err) => {
        if (!cancelled) setError(err instanceof Error ? err.message : 'Failed to load the install log.');
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, []);

  return (
    <div>
      <Title3 block>Install Log</Title3>
      <Body1 block style={{ marginTop: '8px' }}>
        History of configurations applied to the solution (the <code>sys_configs</code> table). The most recent entry
        is the current configuration.
      </Body1>

      {loading && (
        <div style={{ textAlign: 'center', padding: '32px' }}>
          <Spinner size={80} label="Loading install log..." />
        </div>
      )}
      {error && (
        <MessageBar intent="error">
          <MessageBarBody>{error}</MessageBarBody>
        </MessageBar>
      )}

      {!loading && entries && (
        <Card className={styles.card}>
          <Table aria-label="Install log" size="small">
            <TableHeader>
              <TableRow>
                <TableHeaderCell style={{ width: 200 }}>Applied</TableHeaderCell>
                <TableHeaderCell style={{ width: 220 }}>Installed by</TableHeaderCell>
                <TableHeaderCell>Messages</TableHeaderCell>
                <TableHeaderCell style={{ width: 130 }}>Configuration</TableHeaderCell>
              </TableRow>
            </TableHeader>
            <TableBody>
              {entries.length === 0 && (
                <TableRow>
                  <TableCell colSpan={4}>
                    <Text style={{ color: tokens.colorNeutralForeground3 }}>No configurations applied yet.</Text>
                  </TableCell>
                </TableRow>
              )}
              {entries.map((e) => (
                <TableRow key={e.id}>
                  <TableCell>
                    {new Date(e.dateApplied).toLocaleString()}
                    {e.isCurrent && (
                      <Badge appearance="filled" color="brand" size="small" style={{ marginLeft: 8 }}>
                        Current
                      </Badge>
                    )}
                  </TableCell>
                  <TableCell>{e.installedByUser || '—'}</TableCell>
                  <TableCell>
                    {e.messages ? (
                      <Dialog>
                        <DialogTrigger disableButtonEnhancement>
                          <Button appearance="subtle" size="small" icon={<TextBulletListSquare16Regular />}>
                            View log
                          </Button>
                        </DialogTrigger>
                        <DialogSurface className={styles.dialogSurface}>
                          <DialogBody>
                            <DialogTitle>Install log — {new Date(e.dateApplied).toLocaleString()}</DialogTitle>
                            <DialogContent>
                              <pre className={styles.logViewer}>{e.messages}</pre>
                            </DialogContent>
                            <DialogActions>
                              <DialogTrigger disableButtonEnhancement>
                                <Button appearance="primary">Close</Button>
                              </DialogTrigger>
                            </DialogActions>
                          </DialogBody>
                        </DialogSurface>
                      </Dialog>
                    ) : (
                      <Text style={{ color: tokens.colorNeutralForeground3 }}>—</Text>
                    )}
                  </TableCell>
                  <TableCell>
                    {e.configJson ? (
                      <Popover withArrow trapFocus>
                        <PopoverTrigger disableButtonEnhancement>
                          <Button appearance="subtle" size="small" icon={<DocumentText16Regular />}>
                            View config
                          </Button>
                        </PopoverTrigger>
                        <PopoverSurface>
                          <pre className={styles.configJson}>{e.configJson}</pre>
                        </PopoverSurface>
                      </Popover>
                    ) : (
                      <Text style={{ color: tokens.colorNeutralForeground3 }}>—</Text>
                    )}
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </Card>
      )}
    </div>
  );
}
