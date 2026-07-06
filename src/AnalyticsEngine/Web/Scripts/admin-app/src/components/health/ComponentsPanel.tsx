import {
  Badge,
  MessageBar,
  MessageBarBody,
  Table,
  TableHeader,
  TableRow,
  TableHeaderCell,
  TableBody,
  TableCell,
  Text,
} from '@fluentui/react-components';
import { fetchHealthComponents } from '../../api/healthApi';
import { SectionFrame, howLongAgo, statusColor, useHealthSection } from './healthShared';

/** Component health: runtime credential + Service Bus checks, plus App Insights HealthCheck events. */
export default function ComponentsPanel({ active }: { active: boolean }) {
  const state = useHealthSection(fetchHealthComponents, active);

  return (
    <SectionFrame
      title="Component health"
      description="Latest health per component. The runtime credential (expiry) and Service Bus (Teams calls queue) checks run here today; SQL, Activity API, Graph, Key Vault, Redis and DNS fill in as the runtime HealthCheck emitter (a later phase) lands."
      state={state}
    >
      {(data) =>
        data.componentHealthError ? (
          <MessageBar intent="warning">
            <MessageBarBody>Couldn't load component health: {data.componentHealthError}</MessageBarBody>
          </MessageBar>
        ) : data.componentHealth.length > 0 ? (
          <Table size="small" aria-label="Component health">
            <TableHeader>
              <TableRow>
                <TableHeaderCell>Component</TableHeaderCell>
                <TableHeaderCell>Status</TableHeaderCell>
                <TableHeaderCell>Detail</TableHeaderCell>
                <TableHeaderCell>Days to expiry</TableHeaderCell>
                <TableHeaderCell>Last checked</TableHeaderCell>
              </TableRow>
            </TableHeader>
            <TableBody>
              {data.componentHealth.map((c, i) => (
                <TableRow key={(c.component ?? '') + i}>
                  <TableCell>{c.component}</TableCell>
                  <TableCell>
                    <Badge appearance="filled" color={statusColor(c.status)}>
                      {c.status}
                    </Badge>
                  </TableCell>
                  <TableCell>
                    <Text size={200}>{c.detail}</Text>
                  </TableCell>
                  <TableCell>{c.daysToExpiry ?? ''}</TableCell>
                  <TableCell>
                    <Text size={200}>{howLongAgo(c.lastSeenUtc)}</Text>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        ) : (
          <MessageBar intent="info">
            <MessageBarBody>No component health available yet.</MessageBarBody>
          </MessageBar>
        )
      }
    </SectionFrame>
  );
}
