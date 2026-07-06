import {
  Badge,
  MessageBar,
  MessageBarBody,
  Table,
  TableBody,
  TableRow,
  TableCell,
  Text,
  makeStyles,
} from '@fluentui/react-components';
import { fetchHealthConfig } from '../../api/healthApi';
import { SectionFrame, formatUtc, useHealthSection, useHealthStyles } from './healthShared';

const useStyles = makeStyles({
  chips: {
    display: 'flex',
    flexWrap: 'wrap',
    gap: '6px',
    marginTop: '4px',
  },
});

/** Configuration: what's turned on, what this app points at, plus schema/migration + webhook state. */
export default function ConfigPanel({ active }: { active: boolean }) {
  const shared = useHealthStyles();
  const styles = useStyles();
  const state = useHealthSection(fetchHealthConfig, active);

  return (
    <SectionFrame
      title="Configuration"
      description="What's turned on and what this app points at - so an empty section elsewhere reads as &quot;feature off&quot;, not &quot;broken&quot;."
      state={state}
    >
      {(data) => (
        <>
          {data.configError && (
            <MessageBar intent="warning">
              <MessageBarBody>Couldn't load configuration: {data.configError}</MessageBarBody>
            </MessageBar>
          )}

          <Text className={shared.subHeading}>Enabled imports</Text>
          {data.enabledImports.length > 0 ? (
            <div className={styles.chips}>
              {data.enabledImports.map((f) => (
                <Badge key={f} appearance="tint" color="brand">
                  {f}
                </Badge>
              ))}
            </div>
          ) : (
            <Text size={200}>None enabled in this app's config.</Text>
          )}

          <Text className={shared.subHeading}>Schema / migration version</Text>
          {data.schemaError ? (
            <Text size={200}>Couldn't check: {data.schemaError}</Text>
          ) : data.schemaUpToDate === true ? (
            <Badge appearance="filled" color="success">
              Up to date with this build
            </Badge>
          ) : data.schemaUpToDate === false ? (
            <div>
              <Badge appearance="filled" color="danger">
                {data.pendingMigrations.length} migration(s) pending
              </Badge>{' '}
              <Text size={200}>The database is behind this build - run the upgrader. ({data.pendingMigrations.join(', ')})</Text>
            </div>
          ) : (
            <Text size={200}>Unknown.</Text>
          )}

          <Text className={shared.subHeading}>Teams call-records webhook</Text>
          {data.callsImportEnabled ? (
            <div>
              <Badge
                appearance="filled"
                color={
                  data.webhookState === 'Active'
                    ? 'success'
                    : data.webhookState === 'Missing'
                      ? 'warning'
                      : data.webhookState === 'Error'
                        ? 'danger'
                        : 'subtle'
                }
              >
                {data.webhookState}
              </Badge>{' '}
              {data.webhookExpiryUtc && <Text size={200}>expires {formatUtc(data.webhookExpiryUtc)}</Text>}
              {data.webhookDetail && (
                <Text size={200} className={shared.muted}>
                  {data.webhookDetail}
                </Text>
              )}
            </div>
          ) : (
            <Text size={200}>Teams calls import is off.</Text>
          )}

          <Text className={shared.subHeading}>Resources</Text>
          <Table size="small" aria-label="Resources">
            <TableBody>
              <TableRow>
                <TableCell>SQL server</TableCell>
                <TableCell>
                  <Text font="monospace">{data.sqlServer}</Text>
                </TableCell>
              </TableRow>
              <TableRow>
                <TableCell>Redis</TableCell>
                <TableCell>{data.redisHost}</TableCell>
              </TableRow>
              <TableRow>
                <TableCell>Service Bus</TableCell>
                <TableCell>{data.serviceBusEndpoint}</TableCell>
              </TableRow>
              <TableRow>
                <TableCell>Cognitive / Language</TableCell>
                <TableCell>
                  <Text font="monospace">{data.cognitiveEndpoint}</Text>
                </TableCell>
              </TableRow>
              <TableRow>
                <TableCell>Web app URL</TableCell>
                <TableCell>
                  <Text font="monospace">{data.webAppUrl}</Text>
                </TableCell>
              </TableRow>
            </TableBody>
          </Table>
        </>
      )}
    </SectionFrame>
  );
}
