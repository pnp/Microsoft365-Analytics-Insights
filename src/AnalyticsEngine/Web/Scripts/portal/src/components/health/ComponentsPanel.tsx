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
import { useT } from '../../i18n';
import { SectionFrame, healthStatusText, howLongAgo, statusColor, translateHealthComponentDetail, useHealthSection } from './healthShared';

/** Component health: runtime credential + Service Bus checks, plus App Insights HealthCheck events. */
export default function ComponentsPanel({ active }: { active: boolean }) {
  const t = useT();
  const state = useHealthSection(fetchHealthComponents, active);

  return (
    <SectionFrame
      title={t('health.components.title')}
      description={t('health.components.description')}
      state={state}
    >
      {(data) =>
        data.componentHealthError ? (
          <MessageBar intent="warning">
            <MessageBarBody>{t('health.components.loadError', { error: data.componentHealthError })}</MessageBarBody>
          </MessageBar>
        ) : data.componentHealth.length > 0 ? (
          <Table size="small" aria-label={t('health.components.ariaLabel')}>
            <TableHeader>
              <TableRow>
                <TableHeaderCell>{t('health.components.columnComponent')}</TableHeaderCell>
                <TableHeaderCell>{t('health.components.columnStatus')}</TableHeaderCell>
                <TableHeaderCell>{t('health.components.columnDetail')}</TableHeaderCell>
                <TableHeaderCell>{t('health.components.columnDaysToExpiry')}</TableHeaderCell>
                <TableHeaderCell>{t('health.components.columnLastChecked')}</TableHeaderCell>
              </TableRow>
            </TableHeader>
            <TableBody>
              {data.componentHealth.map((c, i) => (
                <TableRow key={(c.component ?? '') + i}>
                  <TableCell>{c.component}</TableCell>
                  <TableCell>
                    <Badge appearance="filled" color={statusColor(c.status)}>
                      {healthStatusText(c.status, t)}
                    </Badge>
                  </TableCell>
                  <TableCell>
                    <Text size={200}>{translateHealthComponentDetail(c, t)}</Text>
                  </TableCell>
                  <TableCell>{c.daysToExpiry ?? ''}</TableCell>
                  <TableCell>
                    <Text size={200}>{howLongAgo(c.lastSeenUtc, t)}</Text>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        ) : (
          <MessageBar intent="info">
            <MessageBarBody>{t('health.components.empty')}</MessageBarBody>
          </MessageBar>
        )
      }
    </SectionFrame>
  );
}
