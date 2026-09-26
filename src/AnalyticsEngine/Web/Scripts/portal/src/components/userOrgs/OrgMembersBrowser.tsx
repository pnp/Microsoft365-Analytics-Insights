import { useEffect, useState } from 'react';
import {
  Badge,
  Button,
  Field,
  Input,
  Link,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  Select,
  Table,
  TableBody,
  TableCell,
  TableHeader,
  TableHeaderCell,
  TableRow,
  Text,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { SearchRegular } from '@fluentui/react-icons';
import Spinner from '../Spinner';
import { fetchOrgMembers, fetchOrgValues } from '../../api/userOrgsApi';
import { formatNumber, useT } from '../../i18n';
import type { UserOrgMemberPage, UserOrgType, UserOrgValuePage } from '../../types/userOrgs';

/** Organisations per page. Enough to scan, few enough to leave the members beside them in view. */
const VALUES_PAGE_SIZE = 25;

/** Users per page. */
const MEMBERS_PAGE_SIZE = 50;

const useStyles = makeStyles({
  panes: { display: 'flex', flexWrap: 'wrap', gap: '24px', alignItems: 'flex-start', marginTop: '12px' },
  orgPane: { flex: '1 1 300px', minWidth: 0, display: 'flex', flexDirection: 'column', gap: '8px' },
  memberPane: { flex: '2 1 420px', minWidth: 0, display: 'flex', flexDirection: 'column', gap: '8px' },
  searchRow: { display: 'flex', gap: '8px' },
  grow: { flexGrow: 1, minWidth: 0 },
  orgLink: { textAlign: 'start', wordBreak: 'break-word' },
  upn: { wordBreak: 'break-all' },
  badge: { marginInlineStart: '8px' },
  pager: { display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: '8px', flexWrap: 'wrap' },
  pagerButtons: { display: 'flex', gap: '8px' },
  muted: { color: tokens.colorNeutralForeground3 },
  heading: { display: 'flex', alignItems: 'center', gap: '8px', minHeight: '24px' },
});

interface Props {
  /** Every org type, in the order the types table shows them. */
  types: UserOrgType[];
  selectedTypeId: number;
  onSelectType: (id: number) => void;
  /**
   * Changes whenever the page reloads its org types - after an import, a save or a delete - so the
   * sizes and memberships shown here are fetched again rather than left stale.
   */
  refreshToken?: unknown;
}

/**
 * "Who is in each organisation?": pick an org type, see its organisations largest first, and pick one
 * to see the users in it. Both lists are searched and paged on the server, because one organisation on
 * a large tenant can hold tens of thousands of people.
 */
export default function OrgMembersBrowser({ types, selectedTypeId, onSelectType, refreshToken }: Props) {
  const t = useT();
  const selected = types.find((type) => type.id === selectedTypeId);

  // Keyed on the type AND on where its values come from. Switching type starts clean - first page,
  // no search, nothing selected - and so does changing the browsed type's source, because that
  // discards every organisation it had: a kept selection would point at one that no longer exists.
  const browserKey = selected
    ? `${selected.id}:${selected.source}:${selected.entraAttributeName ?? ''}`
    : String(selectedTypeId);

  return (
    <div>
      <Field label={t('userOrgs.browse.typeLabel')}>
        <Select
          value={String(selectedTypeId)}
          onChange={(_: unknown, d: { value: string }) => onSelectType(Number(d.value))}
        >
          {types.map((type) => (
            <option key={type.id} value={type.id}>
              {type.name}
            </option>
          ))}
        </Select>
      </Field>
      <TypeBrowser key={browserKey} orgTypeId={selectedTypeId} refreshToken={refreshToken} />
    </div>
  );
}

interface Loaded<T> {
  data: T | null;
  loading: boolean;
  failed: boolean;
}

/**
 * Whether a page came back empty only because the list shrank underneath it - an import or another
 * admin removed rows - leaving the page past the end. Rather than show an empty table and a nonsense
 * "Showing 201-150 of 150", the caller steps back to the last page that has anything on it.
 */
function pastTheEnd(data: { page: number; pageSize: number; total: number; items: unknown[] }): number | null {
  return data.items.length === 0 && data.total > 0 && data.page > 1
    ? Math.max(1, Math.ceil(data.total / data.pageSize))
    : null;
}

function TypeBrowser({ orgTypeId, refreshToken }: { orgTypeId: number; refreshToken?: unknown }) {
  const styles = useStyles();
  const t = useT();

  const [draft, setDraft] = useState('');
  const [search, setSearch] = useState('');
  const [page, setPage] = useState(1);
  // Bumped by Search and "Try again", so asking again with nothing else changed still asks again.
  const [attempt, setAttempt] = useState(0);
  const [values, setValues] = useState<Loaded<UserOrgValuePage>>({ data: null, loading: true, failed: false });
  const [selected, setSelected] = useState<{ id: number; name: string } | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    setValues((s) => ({ ...s, loading: true, failed: false }));
    fetchOrgValues(orgTypeId, { search, page, pageSize: VALUES_PAGE_SIZE }, controller.signal)
      .then((data) => {
        const lastPage = pastTheEnd(data);
        if (lastPage !== null) setPage(lastPage);
        else setValues({ data, loading: false, failed: false });
      })
      .catch(() => {
        if (!controller.signal.aborted) setValues({ data: null, loading: false, failed: true });
      });
    return () => controller.abort();
  }, [orgTypeId, search, page, attempt, refreshToken]);

  const commitSearch = () => {
    setSearch(draft.trim());
    setPage(1);
    setAttempt((n) => n + 1);
  };

  const data = values.data;

  return (
    <div className={styles.panes}>
      <div className={styles.orgPane}>
        <div className={styles.searchRow}>
          <Input
            className={styles.grow}
            value={draft}
            maxLength={250}
            placeholder={t('userOrgs.browse.searchOrgsPlaceholder')}
            aria-label={t('userOrgs.browse.searchOrgsPlaceholder')}
            onChange={(_: unknown, d: { value: string }) => setDraft(d.value)}
            onKeyDown={(e: { key: string }) => {
              if (e.key === 'Enter') commitSearch();
            }}
          />
          <Button icon={<SearchRegular />} onClick={commitSearch}>
            {t('common.action.search')}
          </Button>
        </div>

        {values.failed && (
          <LoadFailed onRetry={() => setAttempt((n) => n + 1)} />
        )}

        {!data && values.loading && <Spinner size={40} label={t('userOrgs.browse.loading')} />}

        {data && data.total === 0 && (
          <Text className={styles.muted}>
            {search ? t('userOrgs.browse.noOrgMatches') : t('userOrgs.browse.noOrgs')}
          </Text>
        )}

        {data && data.total > 0 && (
          <>
            <Table size="small" aria-label={t('userOrgs.browse.orgsTableLabel')}>
              <TableHeader>
                <TableRow>
                  <TableHeaderCell>{t('userOrgs.browse.column.organisation')}</TableHeaderCell>
                  <TableHeaderCell>{t('userOrgs.browse.column.users')}</TableHeaderCell>
                </TableRow>
              </TableHeader>
              <TableBody>
                {data.items.map((value) => {
                  const isSelected = selected?.id === value.id;
                  return (
                    <TableRow key={value.id} appearance={isSelected ? 'brand' : 'none'}>
                      <TableCell>
                        <Link
                          as="button"
                          className={styles.orgLink}
                          aria-pressed={isSelected}
                          onClick={() => setSelected({ id: value.id, name: value.name })}
                        >
                          {value.name}
                        </Link>
                      </TableCell>
                      <TableCell>{formatNumber(value.memberCount)}</TableCell>
                    </TableRow>
                  );
                })}
              </TableBody>
            </Table>
            <Pager
              page={data.page}
              pageSize={data.pageSize}
              total={data.total}
              busy={values.loading}
              onPage={setPage}
            />
          </>
        )}
      </div>

      <div className={styles.memberPane}>
        {selected ? (
          <MemberList
            key={selected.id}
            orgTypeId={orgTypeId}
            valueId={selected.id}
            valueName={selected.name}
            refreshToken={refreshToken}
          />
        ) : (
          <Text className={styles.muted}>{t('userOrgs.browse.pickOrg')}</Text>
        )}
      </div>
    </div>
  );
}

function MemberList({
  orgTypeId,
  valueId,
  valueName,
  refreshToken,
}: {
  orgTypeId: number;
  valueId: number;
  valueName: string;
  refreshToken?: unknown;
}) {
  const styles = useStyles();
  const t = useT();

  const [draft, setDraft] = useState('');
  const [search, setSearch] = useState('');
  const [page, setPage] = useState(1);
  const [attempt, setAttempt] = useState(0);
  const [members, setMembers] = useState<Loaded<UserOrgMemberPage>>({ data: null, loading: true, failed: false });

  useEffect(() => {
    const controller = new AbortController();
    setMembers((s) => ({ ...s, loading: true, failed: false }));
    fetchOrgMembers(orgTypeId, valueId, { search, page, pageSize: MEMBERS_PAGE_SIZE }, controller.signal)
      .then((data) => {
        const lastPage = pastTheEnd(data);
        if (lastPage !== null) setPage(lastPage);
        else setMembers({ data, loading: false, failed: false });
      })
      .catch(() => {
        if (!controller.signal.aborted) setMembers({ data: null, loading: false, failed: true });
      });
    return () => controller.abort();
  }, [orgTypeId, valueId, search, page, attempt, refreshToken]);

  const commitSearch = () => {
    setSearch(draft.trim());
    setPage(1);
    setAttempt((n) => n + 1);
  };

  const data = members.data;

  return (
    <>
      <div className={styles.heading}>
        <Text weight="semibold">{t('userOrgs.browse.membersOf', { name: data?.valueName ?? valueName })}</Text>
        {members.loading && data && <Spinner size={16} />}
      </div>

      <div className={styles.searchRow}>
        <Input
          className={styles.grow}
          value={draft}
          maxLength={250}
          placeholder={t('userOrgs.browse.searchMembersPlaceholder')}
          aria-label={t('userOrgs.browse.searchMembersPlaceholder')}
          onChange={(_: unknown, d: { value: string }) => setDraft(d.value)}
          onKeyDown={(e: { key: string }) => {
            if (e.key === 'Enter') commitSearch();
          }}
        />
        <Button icon={<SearchRegular />} onClick={commitSearch}>
          {t('common.action.search')}
        </Button>
      </div>

      {members.failed && <LoadFailed onRetry={() => setAttempt((n) => n + 1)} />}

      {!data && members.loading && <Spinner size={40} label={t('userOrgs.browse.loading')} />}

      {data && data.total === 0 && (
        <Text className={styles.muted}>
          {search ? t('userOrgs.browse.noMemberMatches') : t('userOrgs.browse.noMembers')}
        </Text>
      )}

      {data && data.total > 0 && (
        <>
          <Table size="small" aria-label={t('userOrgs.browse.membersOf', { name: data.valueName })}>
            <TableHeader>
              <TableRow>
                <TableHeaderCell>{t('userOrgs.browse.column.user')}</TableHeaderCell>
                <TableHeaderCell>{t('userOrgs.browse.column.department')}</TableHeaderCell>
                <TableHeaderCell>{t('userOrgs.browse.column.jobTitle')}</TableHeaderCell>
              </TableRow>
            </TableHeader>
            <TableBody>
              {data.items.map((member) => (
                <TableRow key={member.userId}>
                  <TableCell>
                    <span className={styles.upn}>{member.userPrincipalName}</span>
                    {member.accountEnabled === false && (
                      <Badge className={styles.badge} appearance="tint" color="informative" size="small">
                        {t('userOrgs.browse.accountDisabled')}
                      </Badge>
                    )}
                  </TableCell>
                  <TableCell>{member.department || '—'}</TableCell>
                  <TableCell>{member.jobTitle || '—'}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
          <Pager page={data.page} pageSize={data.pageSize} total={data.total} busy={members.loading} onPage={setPage} />
        </>
      )}
    </>
  );
}

function LoadFailed({ onRetry }: { onRetry: () => void }) {
  const t = useT();
  // The server's own message is deliberately not shown: it is English, and this page is read in
  // Spanish too.
  return (
    <MessageBar intent="error">
      <MessageBarBody>{t('userOrgs.browse.loadFailed')}</MessageBarBody>
      <MessageBarActions>
        <Button size="small" onClick={onRetry}>
          {t('userOrgs.browse.retry')}
        </Button>
      </MessageBarActions>
    </MessageBar>
  );
}

function Pager({
  page,
  pageSize,
  total,
  busy,
  onPage,
}: {
  page: number;
  pageSize: number;
  total: number;
  busy: boolean;
  onPage: (page: number) => void;
}) {
  const styles = useStyles();
  const t = useT();
  const totalPages = Math.max(1, Math.ceil(total / pageSize));
  const from = (page - 1) * pageSize + 1;
  const to = Math.min(page * pageSize, total);

  return (
    <div className={styles.pager}>
      <Text size={200} className={styles.muted}>
        {t('userOrgs.browse.showing', {
          from: formatNumber(from),
          to: formatNumber(to),
          total: formatNumber(total),
        })}
      </Text>
      {totalPages > 1 && (
        <div className={styles.pagerButtons}>
          <Button size="small" disabled={busy || page <= 1} onClick={() => onPage(page - 1)}>
            {t('userOrgs.browse.previous')}
          </Button>
          <Button size="small" disabled={busy || page >= totalPages} onClick={() => onPage(page + 1)}>
            {t('userOrgs.browse.next')}
          </Button>
        </div>
      )}
    </div>
  );
}
