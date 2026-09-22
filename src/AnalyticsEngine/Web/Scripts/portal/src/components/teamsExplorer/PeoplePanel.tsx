import {
  makeStyles,
  tokens,
  Text,
  Button,
  Badge,
  Table,
  TableHeader,
  TableHeaderCell,
  TableBody,
  TableRow,
  TableCell,
  MessageBar,
  MessageBarBody,
} from '@fluentui/react-components';
import { ArrowDownload16Regular } from '@fluentui/react-icons';
import CategoryBarChart from '../charts/CategoryBarChart';
import type { TeamsPeople, TeamsPersonRow } from '../../types/teamsExplorer';
import { useT, useTNode, type TFunction } from '../../i18n';
import {
  SectionCard,
  WindowNote,
  formatCount,
  formatDate,
  queryFor,
  segmentLabel,
  toCategories,
  useTeamsStyles,
} from './teamsShared';

const useStyles = makeStyles({
  muted: {
    color: tokens.colorNeutralForeground3,
  },
  numeric: {
    fontVariantNumeric: 'tabular-nums',
    textAlign: 'right',
  },
  exportRow: {
    display: 'flex',
    justifyContent: 'flex-end',
    marginBottom: '8px',
  },
  upn: {
    wordBreak: 'break-all',
  },
});

const SEGMENT_COLOUR: Record<string, 'success' | 'brand' | 'informative' | 'warning'> = {
  Power: 'success',
  Regular: 'brand',
  Light: 'informative',
  Dormant: 'warning',
};

const REPORTS_READ_ALL = 'Reports.Read.All';

/**
 * People: who to recruit as a champion, and who to help.
 *
 * Names individuals deliberately - a champions programme needs names, and so does an enablement
 * campaign. The privacy note is not decoration: this list is exportable, so whoever downloads it
 * should be reminded what they are about to email around.
 */
export default function PeoplePanel({
  data,
  usageReportsAvailable,
  onExportChampions,
  onExportDormant,
  exporting,
}: {
  data: TeamsPeople;
  usageReportsAvailable: boolean;
  onExportChampions: () => void;
  onExportDormant: () => void;
  exporting: boolean;
}) {
  const styles = useStyles();
  const shared = useTeamsStyles();
  const t = useT();
  const tNode = useTNode();

  if (!usageReportsAvailable) {
    return (
      <MessageBar intent="warning">
        <MessageBarBody>
          {tNode('teamsExplorer.people.usageReportsOff', {
            importName: <strong>{t('teamsExplorer.people.usageReportsOff.importName')}</strong>,
            permission: <strong>{REPORTS_READ_ALL}</strong>,
          })}
        </MessageBarBody>
      </MessageBar>
    );
  }

  return (
    <div>
      <WindowNote window={data.window} includeUsage={false} />

      {data.namesObfuscated && (
        <MessageBar intent="warning" style={{ marginTop: '12px' }}>
          <MessageBarBody>
            {tNode('teamsExplorer.people.namesObfuscated', {
              reports: <em>{t('teamsExplorer.people.namesObfuscated.reports')}</em>,
              setting: <em>{t('teamsExplorer.people.namesObfuscated.setting')}</em>,
            })}
          </MessageBarBody>
        </MessageBar>
      )}

      <MessageBar intent="info" style={{ marginTop: '12px' }}>
        <MessageBarBody>
          {t('teamsExplorer.people.privacyNotice')}
        </MessageBarBody>
      </MessageBar>

      <div className={shared.stack}>
        <SectionCard
          title={t('teamsExplorer.people.champions.title')}
          description={t('teamsExplorer.people.champions.description')}
          query={queryFor(data.queries, 'people-champions')}
          isEmpty={data.champions.length === 0}
        >
          <div className={styles.exportRow}>
            <Button
              appearance="subtle"
              size="small"
              icon={<ArrowDownload16Regular />}
              onClick={onExportChampions}
              disabled={exporting}
            >
              {t('teamsExplorer.action.exportCsv')}
            </Button>
          </div>
          <PeopleTable rows={data.champions} styles={styles} wrapClass={shared.tableWrap} t={t} />
        </SectionCard>

        <SectionCard
          title={t('teamsExplorer.people.dormant.title')}
          description={t('teamsExplorer.people.dormant.description')}
          query={queryFor(data.queries, 'people-dormant')}
          isEmpty={data.dormant.length === 0}
          emptyMessage={t('teamsExplorer.people.dormant.empty')}
          note={
            t('teamsExplorer.people.dormant.note')
          }
        >
          <div className={styles.exportRow}>
            <Button
              appearance="subtle"
              size="small"
              icon={<ArrowDownload16Regular />}
              onClick={onExportDormant}
              disabled={exporting}
            >
              {t('teamsExplorer.action.exportCsv')}
            </Button>
          </div>
          <PeopleTable rows={data.dormant} styles={styles} wrapClass={shared.tableWrap} t={t} />
        </SectionCard>

        <SectionCard
          title={t('teamsExplorer.people.championDepartments.title')}
          description={t('teamsExplorer.people.championDepartments.description')}
          query={queryFor(data.queries, 'people-departments')}
          isEmpty={data.championsByDepartment.length === 0}
          note={
            t('teamsExplorer.people.championDepartments.note')
          }
        >
          <CategoryBarChart
            categories={toCategories(data.championsByDepartment)}
            valueLabel={t('teamsExplorer.people.valueLabel.powerUsers')}
          />
        </SectionCard>
      </div>
    </div>
  );
}

function PeopleTable({
  rows,
  styles,
  wrapClass,
  t,
}: {
  rows: TeamsPersonRow[];
  styles: ReturnType<typeof useStyles>;
  wrapClass: string;
  t: TFunction;
}) {
  return (
    <div className={wrapClass}>
      <Table size="small" aria-label={t('teamsExplorer.people.table.aria')}>
        <TableHeader>
          <TableRow>
            <TableHeaderCell>{t('teamsExplorer.column.user')}</TableHeaderCell>
            <TableHeaderCell>{t('teamsExplorer.column.department')}</TableHeaderCell>
            <TableHeaderCell>{t('teamsExplorer.column.segment')}</TableHeaderCell>
            <TableHeaderCell>{t('teamsExplorer.column.activeDays')}</TableHeaderCell>
            <TableHeaderCell>{t('teamsExplorer.column.channel')}</TableHeaderCell>
            <TableHeaderCell>{t('teamsExplorer.column.privateChat')}</TableHeaderCell>
            <TableHeaderCell>{t('teamsExplorer.column.organised')}</TableHeaderCell>
            <TableHeaderCell>{t('teamsExplorer.column.attended')}</TableHeaderCell>
            <TableHeaderCell>{t('teamsExplorer.column.callsHosted')}</TableHeaderCell>
            <TableHeaderCell>{t('teamsExplorer.column.callsJoined')}</TableHeaderCell>
            <TableHeaderCell>{t('teamsExplorer.column.lastSeen')}</TableHeaderCell>
          </TableRow>
        </TableHeader>
        <TableBody>
          {rows.map((row) => (
            <TableRow key={row.userPrincipalName}>
              <TableCell className={styles.upn}>{row.userPrincipalName}</TableCell>
              <TableCell>{row.department ?? '\u2014'}</TableCell>
              <TableCell>
                <Badge appearance="tint" color={SEGMENT_COLOUR[row.segment] ?? 'informative'}>
                  {segmentLabel(t, row.segment)}
                </Badge>
              </TableCell>
              <TableCell className={styles.numeric}>{formatCount(row.activeDays)}</TableCell>
              <TableCell className={styles.numeric}>{formatCount(row.channelMessages)}</TableCell>
              <TableCell className={styles.numeric}>{formatCount(row.privateMessages)}</TableCell>
              <TableCell className={styles.numeric}>{formatCount(row.meetingsOrganised)}</TableCell>
              <TableCell className={styles.numeric}>{formatCount(row.meetingsAttended)}</TableCell>
              <TableCell className={styles.numeric}>{formatCount(row.callsHosted)}</TableCell>
              <TableCell className={styles.numeric}>{formatCount(row.callsAttended)}</TableCell>
              <TableCell>
                <Text size={200} className={styles.muted}>
                  {formatDate(row.lastActivity)}
                </Text>
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </div>
  );
}
