import { useCallback, useEffect, useState } from 'react';
import {
  Badge,
  Button,
  Card,
  CardHeader,
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
import OrgTypeDialog from '../components/userOrgs/OrgTypeDialog';
import { createOrgType, deleteOrgType, fetchOrgTypes, updateOrgType } from '../api/userOrgsApi';
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

  const [types, setTypes] = useState<UserOrgType[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [dialogOpen, setDialogOpen] = useState(false);
  const [editing, setEditing] = useState<UserOrgType | null>(null);

  const load = useCallback(async () => {
    try {
      setTypes(await fetchOrgTypes());
      setError(null);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not load the organisation types.');
    }
  }, []);

  useEffect(() => {
    load();
  }, [load]);

  const onSave = async (model: UserOrgTypeSave) => {
    if (editing) {
      await updateOrgType(editing.id, model);
      toast.success(`Saved ${model.name}.`);
    } else {
      await createOrgType(model);
      toast.success(`Created ${model.name}.`);
    }
    setDialogOpen(false);
    setEditing(null);
    await load();
  };

  const onDelete = async (type: UserOrgType) => {
    // A confirm() rather than a dialog: this destroys every assignment under the type, and the count
    // being destroyed is the thing the admin needs in front of them when they decide.
    const message =
      `Delete "${type.name}"?\n\n` +
      `This removes the organisation values of ${type.assignedUserCount.toLocaleString()} user(s) ` +
      `and cannot be undone.`;
    if (!window.confirm(message)) return;

    try {
      await deleteOrgType(type.id);
      toast.success(`Deleted ${type.name}.`);
      await load();
    } catch (e) {
      toast.error(e instanceof Error ? e.message : 'Could not delete the organisation type.');
    }
  };

  if (!types && !error) {
    return (
      <div style={{ textAlign: 'center', padding: '32px' }}>
        <Spinner size={100} label="Loading organisation types..." />
      </div>
    );
  }

  const csvTypes = (types ?? []).filter((t) => t.source === 'csv');

  return (
    <div>
      <div className={styles.toolbar}>
        <Title3 block>User organisations</Title3>
        <Button
          appearance="primary"
          icon={<Add20Regular />}
          onClick={() => {
            setEditing(null);
            setDialogOpen(true);
          }}
        >
          New organisation type
        </Button>
      </div>

      <Text block className={styles.muted}>
        Group users by something your directory does not track reliably - a cost centre, a business
        unit, a team from an HR export. Each type takes its values either from a custom Microsoft Entra
        attribute, read on every user import, or from a CSV you upload here. A user has at most one
        value per type.
      </Text>

      {error && (
        <MessageBar intent="error">
          <MessageBarBody>{error}</MessageBarBody>
        </MessageBar>
      )}

      <div className={styles.cards}>
        <Card>
          <CardHeader header={<Subtitle2>Organisation types</Subtitle2>} />
          {types && types.length === 0 ? (
            <Text className={styles.muted}>
              None defined yet. Create one to start grouping users.
            </Text>
          ) : (
            <Table size="small" aria-label="Organisation types">
              <TableHeader>
                <TableRow>
                  <TableHeaderCell>Name</TableHeaderCell>
                  <TableHeaderCell>Source</TableHeaderCell>
                  <TableHeaderCell>Users assigned</TableHeaderCell>
                  <TableHeaderCell>Distinct values</TableHeaderCell>
                  <TableHeaderCell>State</TableHeaderCell>
                  <TableHeaderCell>Actions</TableHeaderCell>
                </TableRow>
              </TableHeader>
              <TableBody>
                {(types ?? []).map((type) => (
                  <TableRow key={type.id}>
                    <TableCell>{type.name}</TableCell>
                    <TableCell>
                      {type.source === 'entra' ? (
                        <span>
                          Entra attribute{' '}
                          <Text className={styles.mono}>{type.entraAttributeName}</Text>
                        </span>
                      ) : (
                        <span>
                          CSV upload
                          {type.lastImport && (
                            <Text size={200} block className={styles.muted}>
                              last imported {new Date(type.lastImport.queuedUtc).toLocaleString()} by{' '}
                              {type.lastImport.startedBy ?? 'unknown'} ({type.lastImport.status})
                            </Text>
                          )}
                        </span>
                      )}
                    </TableCell>
                    <TableCell>{type.assignedUserCount.toLocaleString()}</TableCell>
                    <TableCell>{type.distinctValueCount.toLocaleString()}</TableCell>
                    <TableCell>
                      {type.isEnabled ? (
                        <Badge appearance="tint" color="success">
                          Enabled
                        </Badge>
                      ) : (
                        <Badge appearance="tint" color="informative">
                          Disabled
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
                          Edit
                        </Button>
                        <Button
                          appearance="subtle"
                          size="small"
                          icon={<Delete16Regular />}
                          onClick={() => onDelete(type)}
                        >
                          Delete
                        </Button>
                      </div>
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          )}
        </Card>

        {csvTypes.map((type) => (
          <Card key={type.id}>
            <CardHeader header={<Subtitle2>Import {type.name} from a file</Subtitle2>} />
            <Text block className={styles.muted}>
              A CSV with a user column and an organisation column, in either order. A row with a blank
              organisation clears that user's value.
            </Text>
            <CsvImportPanel orgType={type} onImportFinished={load} />
          </Card>
        ))}

        {(types ?? []).some((t) => t.source === 'entra') && (
          <Card>
            <CardHeader header={<Subtitle2>How Entra-sourced types are kept up to date</Subtitle2>} />
            <Text block>
              These are read during the normal user import, so values appear after the next import
              cycle. Adding an Entra organisation type, or pointing one at a different attribute, makes
              the next cycle re-read every user once so the new attribute is populated for people who
              have not otherwise changed - that one cycle takes longer than usual.
            </Text>
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
