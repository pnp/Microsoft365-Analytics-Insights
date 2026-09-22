import { useEffect, useMemo, useState } from 'react';
import {
  Button,
  Combobox,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  Input,
  MessageBar,
  MessageBarBody,
  Option,
  Radio,
  RadioGroup,
  Switch,
  Text,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { fetchAttributeCatalogue, testEntraAttribute } from '../../api/userOrgsApi';
import type {
  UserOrgAttributeCatalogue,
  UserOrgSource,
  UserOrgTestResult,
  UserOrgType,
  UserOrgTypeSave,
} from '../../types/userOrgs';

const useStyles = makeStyles({
  form: { display: 'flex', flexDirection: 'column', gap: '14px', minWidth: '520px' },
  testRow: { display: 'flex', gap: '8px', alignItems: 'flex-end' },
  testUpn: { flexGrow: 1 },
  resultGrid: {
    display: 'grid',
    gridTemplateColumns: 'max-content 1fr',
    columnGap: '16px',
    rowGap: '4px',
    marginTop: '8px',
  },
  label: { fontWeight: tokens.fontWeightSemibold, color: tokens.colorNeutralForeground2 },
  mono: { fontFamily: tokens.fontFamilyMonospace, wordBreak: 'break-all' },
  muted: { color: tokens.colorNeutralForeground3 },
});

export interface OrgTypeDialogProps {
  open: boolean;
  /** The type being edited, or null to create a new one. */
  editing: UserOrgType | null;
  onDismiss: () => void;
  onSave: (model: UserOrgTypeSave) => Promise<void>;
}

/**
 * Create or edit an org type.
 *
 * For an Entra-sourced type the attribute must be proved against a real user before it can be saved.
 * That is not a nicety: Microsoft Graph fails an entire `/users/delta` request when `$select` names
 * a property it does not recognise, so an unchecked typo would stop user metadata importing
 * altogether until the importer's fallback noticed.
 */
export default function OrgTypeDialog({ open, editing, onDismiss, onSave }: OrgTypeDialogProps) {
  const styles = useStyles();

  const [name, setName] = useState('');
  const [source, setSource] = useState<UserOrgSource>('entra');
  const [attribute, setAttribute] = useState('');
  const [isEnabled, setIsEnabled] = useState(true);

  const [catalogue, setCatalogue] = useState<UserOrgAttributeCatalogue | null>(null);
  const [testUpn, setTestUpn] = useState('');
  const [testing, setTesting] = useState(false);
  const [testResult, setTestResult] = useState<UserOrgTestResult | null>(null);
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);

  // The attribute that was last proved to work. Saving is gated on this matching what is in the box,
  // so editing the attribute after a successful test re-arms the requirement.
  const [provenAttribute, setProvenAttribute] = useState<string | null>(null);

  useEffect(() => {
    if (!open) return;
    setName(editing?.name ?? '');
    setSource(editing?.source ?? 'entra');
    setAttribute(editing?.entraAttributeName ?? '');
    setIsEnabled(editing?.isEnabled ?? true);
    setTestResult(null);
    setSaveError(null);
    // An attribute that is already saved was proved when it was saved, so editing an unchanged type
    // does not force the admin to re-test it.
    setProvenAttribute(editing?.entraAttributeName ?? null);
  }, [open, editing]);

  useEffect(() => {
    if (!open || catalogue) return;
    let cancelled = false;
    fetchAttributeCatalogue()
      .then((c) => {
        if (!cancelled) setCatalogue(c);
      })
      .catch(() => {
        // Discovery is optional - the admin can always type a name in full and test it.
      });
    return () => {
      cancelled = true;
    };
  }, [open, catalogue]);

  const attributeOptions = useMemo(() => {
    if (!catalogue) return [] as string[];
    return [
      ...catalogue.extensionAttributes,
      ...catalogue.builtInProperties,
      ...catalogue.employeeOrgDataProperties,
      ...catalogue.directoryExtensions.map((d) => d.name),
    ];
  }, [catalogue]);

  const needsProof = source === 'entra';
  const attributeProven =
    !needsProof ||
    (provenAttribute !== null &&
      provenAttribute.trim().toLowerCase() === attribute.trim().toLowerCase());

  const canSave =
    name.trim().length > 0 && (!needsProof || attribute.trim().length > 0) && attributeProven;

  const runTest = async () => {
    setTesting(true);
    setTestResult(null);
    try {
      const result = await testEntraAttribute(attribute, testUpn);
      setTestResult(result);
      // A user who simply has no value still proves the attribute is readable, which is what saving
      // is gated on - the attribute existing, not this particular person having a value for it.
      setProvenAttribute(result.succeeded ? attribute : null);
    } catch (e) {
      setTestResult({
        succeeded: false,
        upn: testUpn,
        attributeName: attribute,
        graphProperty: null,
        rawValue: null,
        normalisedValue: null,
        wouldTruncate: false,
        hasNoValue: false,
        message: e instanceof Error ? e.message : 'The test failed.',
      });
      setProvenAttribute(null);
    } finally {
      setTesting(false);
    }
  };

  const save = async () => {
    setSaving(true);
    setSaveError(null);
    try {
      await onSave({
        name: name.trim(),
        source,
        entraAttributeName: source === 'entra' ? attribute.trim() : null,
        isEnabled,
      });
    } catch (e) {
      setSaveError(e instanceof Error ? e.message : 'Could not save the organisation type.');
    } finally {
      setSaving(false);
    }
  };

  return (
    <Dialog open={open} onOpenChange={(_e, data) => !data.open && onDismiss()}>
      <DialogSurface mountNode={undefined}>
        <DialogBody>
          <DialogTitle>{editing ? `Edit ${editing.name}` : 'New organisation type'}</DialogTitle>
          <DialogContent>
            <div className={styles.form}>
              <Field
                label="Name"
                required
                hint="The label this grouping is shown under on the user lookup page, for example Cost Centre. Organisation types are not yet available as a filter on the reports."
              >
                <Input value={name} onChange={(_e, d) => setName(d.value)} maxLength={100} />
              </Field>

              <Field label="Where the values come from">
                <RadioGroup
                  value={source}
                  onChange={(_e, d) => {
                    setSource(d.value as UserOrgSource);
                    setTestResult(null);
                  }}
                >
                  <Radio
                    value="entra"
                    label="A custom Microsoft Entra attribute, read on every user import"
                  />
                  <Radio value="csv" label="A CSV file uploaded here" />
                </RadioGroup>
              </Field>

              {editing &&
                editing.assignedUserCount > 0 &&
                (source !== editing.source ||
                  (source === 'entra' && attribute.trim() !== (editing.entraAttributeName ?? ''))) && (
                  <MessageBar intent="warning">
                    <MessageBarBody>
                      Saving this discards the {editing.assignedUserCount.toLocaleString()} value
                      {editing.assignedUserCount === 1 ? '' : 's'} this type holds today. They were read
                      from a source that will no longer be the source of truth for it, so leaving them
                      would show stale values indefinitely — a CSV Merge in particular never touches
                      users the file does not mention.
                    </MessageBarBody>
                  </MessageBar>
                )}

              {source === 'entra' && (
                <>
                  <Field
                    label="Entra attribute"
                    required
                    hint="One of extensionAttribute1-15, employeeId, employeeType, employeeOrgData.costCenter, employeeOrgData.division, a directory extension (extension_{appId}_{name}) or a schema extension."
                  >
                    <Combobox
                      freeform
                      value={attribute}
                      selectedOptions={attribute ? [attribute] : []}
                      onInput={(e) => setAttribute((e.target as HTMLInputElement).value)}
                      onOptionSelect={(_e, d) => setAttribute(d.optionValue ?? '')}
                      placeholder="extensionAttribute1"
                    >
                      {attributeOptions.map((o) => (
                        <Option key={o} value={o}>
                          {o}
                        </Option>
                      ))}
                    </Combobox>
                  </Field>

                  {catalogue?.discoveryWarning && (
                    <MessageBar intent="info">
                      <MessageBarBody>{catalogue.discoveryWarning}</MessageBarBody>
                    </MessageBar>
                  )}

                  <Field
                    label="Test it against a user"
                    hint="Required before saving. Microsoft Graph rejects the whole user import if it does not recognise the attribute, so it has to be proved first."
                  >
                    <div className={styles.testRow}>
                      <Input
                        className={styles.testUpn}
                        value={testUpn}
                        onChange={(_e, d) => setTestUpn(d.value)}
                        placeholder="someone@contoso.com"
                      />
                      <Button
                        onClick={runTest}
                        disabled={
                          testing || attribute.trim().length === 0 || testUpn.trim().length === 0
                        }
                      >
                        {testing ? 'Testing...' : 'Test'}
                      </Button>
                    </div>
                  </Field>

                  {testResult && <TestOutcome result={testResult} styles={styles} />}

                  {!attributeProven && !testResult && (
                    <Text size={200} className={styles.muted}>
                      Test the attribute against a user before saving.
                    </Text>
                  )}
                </>
              )}

              <Switch
                checked={isEnabled}
                onChange={(_e, d) => setIsEnabled(d.checked)}
                label={isEnabled ? 'Enabled - included in imports' : 'Disabled - not imported'}
              />

              {saveError && (
                <MessageBar intent="error">
                  <MessageBarBody>{saveError}</MessageBarBody>
                </MessageBar>
              )}
            </div>
          </DialogContent>
          <DialogActions>
            <Button appearance="secondary" onClick={onDismiss}>
              Cancel
            </Button>
            <Button appearance="primary" onClick={save} disabled={!canSave || saving}>
              {saving ? 'Saving...' : 'Save'}
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}

function TestOutcome({
  result,
  styles,
}: {
  result: UserOrgTestResult;
  styles: ReturnType<typeof useStyles>;
}) {
  if (!result.succeeded) {
    return (
      <MessageBar intent="error">
        <MessageBarBody>{result.message ?? 'The attribute could not be read.'}</MessageBarBody>
      </MessageBar>
    );
  }

  return (
    <div>
      <MessageBar intent={result.hasNoValue ? 'warning' : 'success'}>
        <MessageBarBody>{result.message ?? 'The attribute was read successfully.'}</MessageBarBody>
      </MessageBar>
      <div className={styles.resultGrid}>
        <Text className={styles.label}>Graph property</Text>
        <Text className={styles.mono}>{result.graphProperty ?? '—'}</Text>
        <Text className={styles.label}>Value from Graph</Text>
        <Text className={styles.mono}>{result.rawValue ?? '—'}</Text>
        <Text className={styles.label}>Stored as</Text>
        <Text className={styles.mono}>{result.normalisedValue ?? '—'}</Text>
      </div>
    </div>
  );
}
