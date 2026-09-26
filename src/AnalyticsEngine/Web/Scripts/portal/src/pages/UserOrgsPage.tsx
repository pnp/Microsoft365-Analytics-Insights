import { useCallback, useEffect, useRef, useState } from 'react';
import {
  Badge,
  Button,
  Card,
  CardHeader,
  Link,
  MessageBar,
  MessageBarBody,
  Subtitle2,
  Table,
  TableBody,
  TableCell,
  TableHeader,
  TableHeaderCell,
  TableRow,
  Text,
  Title3,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { Add20Regular, Delete16Regular, Edit16Regular } from '@fluentui/react-icons';
import Spinner from '../components/Spinner';
import toast from '../components/toast';
import CsvImportPanel from '../components/userOrgs/CsvImportPanel';
import OrgMembersBrowser from '../components/userOrgs/OrgMembersBrowser';
import OrgTypeDialog from '../components/userOrgs/OrgTypeDialog';
import { createOrgType, deleteOrgType, fetchOrgTypes, updateOrgType } from '../api/userOrgsApi';
import { formatDateParts, formatNumber, plural, useT } from '../i18n';
import { neverRefreshedKey, STATUS_KEYS } from '../components/userOrgs/userOrgShared';
import type { UserOrgType, UserOrgTypeSave } from '../types/userOrgs';

const useStyles = makeStyles({
  cards: { display: 'flex', flexDirection: 'column', gap: '16px', marginTop: '16px' },
  toolbar: { display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: '12px' },
  mono: { fontFamily: tokens.fontFamilyMonospace, wordBreak: 'break-all' },
  muted: { color: tokens.colorNeutralForeground3 },
  actions: { display: 'flex', gap: '4px' },
});

/**
 * Administration -> User organisations.
 *
 * Tenants whose Entra `department` / `jobTitle` metadata is unreliable keep their real org structure
 * somewhere else - an HR or finance system - which either syncs into a custom Entra attribute or can
 * only be exported as a spreadsheet. This page defines those groupings and keeps them populated.
 */
export default function UserOrgsPage() {
  const styles = useStyles();
  const t = useT();

  const [types, setTypes] = useState<UserOrgType[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [dialogOpen, setDialogOpen] = useState(false);
  const [editing, setEditing] = useState<UserOrgType | null>(null);
  const [browseTypeId, setBrowseTypeId] = useState<number | null>(null);
  const browseRef = useRef<HTMLDivElement>(null);

  // Browse the first type until the admin picks another, and move on if the one being browsed is
  // deleted - otherwise the panel would keep asking for a type that no longer exists.
  useEffect(() => {
    if (!types || types.length === 0) {
      setBrowseTypeId(null);
    } else if (browseTypeId === null || !types.some((type) => type.id === browseTypeId)) {
      setBrowseTypeId(types[0].id);
    }
  }, [types, browseTypeId]);

  const viewUsers = (type: UserOrgType) => {
    setBrowseTypeId(type.id);
    const card = browseRef.current;
    card?.scrollIntoView?.({ behavior: 'smooth', block: 'start' });
    // Move focus with the view, so keyboard and screen-reader users land on the list they asked for
    // rather than on a link that has just scrolled out of sight. preventScroll keeps the smooth scroll.
    card?.querySelector('select')?.focus({ preventScroll: true });
  };

  const load = useCallback(async () => {
    try {
      setTypes(await fetchOrgTypes());
      setError(null);
    } catch (e) {
      setError(e instanceof Error ? e.message : t('errors.userOrgs.loadFailed'));
    }
  }, [t]);

  useEffect(() => {
    load();
  }, [load]);

  const onSave = async (model: UserOrgTypeSave) => {
    if (editing) {
      await updateOrgType(editing.id, model);
      toast.success(t('userOrgs.toast.saved', { name: model.name }));
    } else {
      await createOrgType(model);
      toast.success(t('userOrgs.toast.created', { name: model.name }));
    }
    setDialogOpen(false);
    setEditing(null);
    await load();
  };

  const onDelete = async (type: UserOrgType) => {
    // A confirm() rather than a dialog: this destroys every assignment under the type, and the count
    // being destroyed is the thing the admin needs in front of them when they decide.
    const message = t('userOrgs.delete.confirm', {
      name: type.name,
      count: formatNumber(type.assignedUserCount),
    });
    if (!window.confirm(message)) return;

    try {
      await deleteOrgType(type.id);
      toast.success(t('userOrgs.toast.deleted', { name: type.name }));
      await load();
    } catch (e) {
      toast.error(e instanceof Error ? e.message : t('errors.userOrgs.deleteFailed'));
    }
  };

  if (!types && !error) {
    return (
      <div style={{ textAlign: 'center', padding: '32px' }}>
        <Spinner size={100} label={t('userOrgs.page.loading')} />
      </div>
    );
  }

  // Disabled types are deliberately excluded: the toggle is labelled "not imported", and the server
  // refuses an upload into a disabled type, so offering the panel would only produce an error.
  const csvTypes = (types ?? []).filter((t) => t.source === 'csv' && t.isEnabled);

  return (
    <div>
      <div className={styles.toolbar}>
        <Title3 block>{t('userOrgs.page.title')}</Title3>
        <Button
          appearance="primary"
          icon={<Add20Regular />}
          onClick={() => {
            setEditing(null);
            setDialogOpen(true);
          }}
        >
          {t('userOrgs.page.new')}
        </Button>
      </div>

      <Text block className={styles.muted}>
        {t('userOrgs.page.intro')}
      </Text>

      {error && (
        <MessageBar intent="error">
          <MessageBarBody>{error}</MessageBarBody>
        </MessageBar>
      )}

      <div className={styles.cards}>
        <Card>
          <CardHeader header={<Subtitle2>{t('userOrgs.types.title')}</Subtitle2>} />
          {types && types.length === 0 ? (
            <Text className={styles.muted}>{t('userOrgs.types.empty')}</Text>
          ) : (
            <Table size="small" aria-label={t('userOrgs.types.title')}>
              <TableHeader>
                <TableRow>
                  <TableHeaderCell>{t('userOrgs.column.name')}</TableHeaderCell>
                  <TableHeaderCell>{t('userOrgs.column.source')}</TableHeaderCell>
                  <TableHeaderCell>{t('userOrgs.column.lastRefreshed')}</TableHeaderCell>
                  <TableHeaderCell>{t('userOrgs.column.usersAssigned')}</TableHeaderCell>
                  <TableHeaderCell>{t('userOrgs.column.distinctValues')}</TableHeaderCell>
                  <TableHeaderCell>{t('userOrgs.column.state')}</TableHeaderCell>
                  <TableHeaderCell>{t('userOrgs.column.actions')}</TableHeaderCell>
                </TableRow>
              </TableHeader>
              <TableBody>
                {(types ?? []).map((type) => (
                  <TableRow key={type.id}>
                    <TableCell>{type.name}</TableCell>
                    <TableCell>
                      {type.source === 'entra' ? (
                        <span>
                          {t('userOrgs.source.entra')}{' '}
                          <Text className={styles.mono}>{type.entraAttributeName}</Text>
                        </span>
                      ) : (
                        <span>
                          {t('userOrgs.source.csv')}
                          {type.lastImport && (
                            <Text size={200} block className={styles.muted}>
                              {t('userOrgs.source.lastImported', {
                                when: formatDateParts(new Date(type.lastImport.queuedUtc), {
                                  dateStyle: 'short',
                                  timeStyle: 'short',
                                }),
                                who: type.lastImport.startedBy ?? t('userOrgs.source.unknownUser'),
                                status: t(STATUS_KEYS[type.lastImport.status]),
                              })}
                            </Text>
                          )}
                        </span>
                      )}
                    </TableCell>
                    <TableCell>
                      {type.lastRefreshedUtc ? (
                        formatDateParts(new Date(type.lastRefreshedUtc), {
                          dateStyle: 'short',
                          timeStyle: 'short',
                        })
                      ) : (
                        <Text className={styles.muted}>{t(neverRefreshedKey(type))}</Text>
                      )}
                    </TableCell>
                    <TableCell>
                      {type.assignedUserCount > 0 ? (
                        // The count opens "who is in each organisation" for this type. A link rather
                        // than another action button, which the Actions column has no room for.
                        <Link
                          as="button"
                          onClick={() => viewUsers(type)}
                          aria-label={t(
                            plural(type.assignedUserCount, 'userOrgs.types.viewUsers.one', 'userOrgs.types.viewUsers.other'),
                            { count: formatNumber(type.assignedUserCount), name: type.name },
                          )}
                        >
                          {formatNumber(type.assignedUserCount)}
                        </Link>
                      ) : (
                        formatNumber(type.assignedUserCount)
                      )}
                    </TableCell>
                    <TableCell>{formatNumber(type.distinctValueCount)}</TableCell>
                    <TableCell>
                      {type.isEnabled ? (
                        <Badge appearance="tint" color="success">
                          {t('userOrgs.state.enabled')}
                        </Badge>
                      ) : (
                        <Badge appearance="tint" color="informative">
                          {t('userOrgs.state.disabled')}
                        </Badge>
                      )}
                    </TableCell>
                    <TableCell>
                      <div className={styles.actions}>
                        <Button
                          appearance="subtle"
                          size="small"
                          icon={<Edit16Regular />}
                          onClick={() => {
                            setEditing(type);
                            setDialogOpen(true);
                          }}
                        >
                          {t('userOrgs.action.edit')}
                        </Button>
                        <Button
                          appearance="subtle"
                          size="small"
                          icon={<Delete16Regular />}
                          onClick={() => onDelete(type)}
                        >
                          {t('userOrgs.action.delete')}
                        </Button>
                      </div>
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          )}
        </Card>

        {types && types.length > 0 && browseTypeId !== null && (
          <div ref={browseRef}>
            <Card>
              <CardHeader header={<Subtitle2>{t('userOrgs.browse.title')}</Subtitle2>} />
              <Text block className={styles.muted}>
                {t('userOrgs.browse.intro')}
              </Text>
              <OrgMembersBrowser
                types={types}
                selectedTypeId={browseTypeId}
                onSelectType={setBrowseTypeId}
                refreshToken={types}
              />
            </Card>
          </div>
        )}

        {csvTypes.map((type) => (
          <Card key={type.id}>
            <CardHeader
              header={<Subtitle2>{t('userOrgs.import.cardTitle', { name: type.name })}</Subtitle2>}
            />
            <Text block className={styles.muted}>
              {t('userOrgs.import.cardIntro')}
            </Text>
            <CsvImportPanel orgType={type} onImportFinished={load} />
          </Card>
        ))}

        {(types ?? []).some((t2) => t2.source === 'entra') && (
          <Card>
            <CardHeader header={<Subtitle2>{t('userOrgs.entraCard.title')}</Subtitle2>} />
            <Text block>{t('userOrgs.entraCard.body')}</Text>
            <Text block>{t('userOrgs.entraCard.lastRefreshed')}</Text>
          </Card>
        )}
      </div>

      <OrgTypeDialog
        open={dialogOpen}
        editing={editing}
        onDismiss={() => {
          setDialogOpen(false);
          setEditing(null);
        }}
        onSave={onSave}
      />
    </div>
  );
}
