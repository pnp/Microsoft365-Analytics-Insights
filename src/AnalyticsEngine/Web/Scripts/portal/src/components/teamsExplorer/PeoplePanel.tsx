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
import {
  SectionCard,
  WindowNote,
  formatCount,
  formatDate,
  queryFor,
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

  if (!usageReportsAvailable) {
    return (
      <MessageBar intent="warning">
        <MessageBarBody>
          The Microsoft 365 usage reports import is switched off, so there is no per-user Teams
          activity and nobody can be ranked. Enable <strong>Graph usage reports</strong> in the
          installer and grant the runtime app <strong>Reports.Read.All</strong>.
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
            The usage reports appear to carry anonymised user names, so the people below cannot be
            identified. This is a Microsoft 365 admin centre setting - <em>Reports</em> &gt;{' '}
            <em>Display concealed user, group, and site names in all reports</em> - and it is applied
            by Microsoft before the data ever reaches this product. Turn it off in the admin centre
            if you need named adoption reporting.
          </MessageBarBody>
        </MessageBar>
      )}

      <MessageBar intent="info" style={{ marginTop: '12px' }}>
        <MessageBarBody>
          These lists name individuals. They are intended for running a champions programme or an
          enablement campaign, not for performance management - Teams activity measures how someone
          works, not how well.
        </MessageBarBody>
      </MessageBar>

      <div className={shared.stack}>
        <SectionCard
          title="Teams champions"
          description="The people getting the most out of Teams - your best enablement recruits."
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
              Export CSV
            </Button>
          </div>
          <PeopleTable rows={data.champions} styles={styles} wrapClass={shared.tableWrap} />
        </SectionCard>

        <SectionCard
          title="Dormant users"
          description="People the usage reports covered who did nothing in Teams at all."
          query={queryFor(data.queries, 'people-dormant')}
          isEmpty={data.dormant.length === 0}
          emptyMessage="Everyone the usage reports covered did something in Teams this period."
          note={
            'Ordered by how long ago they were last seen, so the people who have drifted furthest '
            + 'come first.'
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
              Export CSV
            </Button>
          </div>
          <PeopleTable rows={data.dormant} styles={styles} wrapClass={shared.tableWrap} />
        </SectionCard>

        <SectionCard
          title="Where the champions are"
          description="Departments with the most power users."
          query={queryFor(data.queries, 'people-departments')}
          isEmpty={data.championsByDepartment.length === 0}
          note={
            'A power user is active on more than 60% of the period\u2019s working days. Departments '
            + 'with many are where a champions programme already has a foothold.'
          }
        >
          <CategoryBarChart
            categories={toCategories(data.championsByDepartment)}
            valueLabel="power users"
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
}: {
  rows: TeamsPersonRow[];
  styles: ReturnType<typeof useStyles>;
  wrapClass: string;
}) {
  return (
    <div className={wrapClass}>
      <Table size="small" aria-label="Teams users">
        <TableHeader>
          <TableRow>
            <TableHeaderCell>User</TableHeaderCell>
            <TableHeaderCell>Department</TableHeaderCell>
            <TableHeaderCell>Segment</TableHeaderCell>
            <TableHeaderCell>Active days</TableHeaderCell>
            <TableHeaderCell>Channel</TableHeaderCell>
            <TableHeaderCell>Private chat</TableHeaderCell>
            <TableHeaderCell>Organised</TableHeaderCell>
            <TableHeaderCell>Attended</TableHeaderCell>
            <TableHeaderCell>Calls hosted</TableHeaderCell>
            <TableHeaderCell>Calls joined</TableHeaderCell>
            <TableHeaderCell>Last seen</TableHeaderCell>
          </TableRow>
        </TableHeader>
        <TableBody>
          {rows.map((row) => (
            <TableRow key={row.userPrincipalName}>
              <TableCell className={styles.upn}>{row.userPrincipalName}</TableCell>
              <TableCell>{row.department ?? '\u2014'}</TableCell>
              <TableCell>
                <Badge appearance="tint" color={SEGMENT_COLOUR[row.segment] ?? 'informative'}>
                  {row.segment}
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
