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
import { formatNumber, plural, useT, type TFunction } from '../../i18n';
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
  const t = useT();

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

  // A disabled type is never read, so its attribute cannot break the user import and does not have
  // to be proved. That matters for recovery rather than convenience: when a directory extension is
  // deleted from the tenant the import tells the admin to fix the type here, and turning it off is
  // the only fix that keeps the values - so demanding a successful test first would leave them with
  // no non-destructive option at all.
  const needsProof = source === 'entra' && isEnabled;
  const attributeProven =
    !needsProof ||
    (provenAttribute !== null &&
      provenAttribute.trim().toLowerCase() === attribute.trim().toLowerCase());

  const canSave =
    name.trim().length > 0 &&
    (source !== 'entra' || attribute.trim().length > 0) &&
    attributeProven;

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
        message: e instanceof Error ? e.message : t('errors.userOrgs.testFailed'),
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
      setSaveError(e instanceof Error ? e.message : t('errors.userOrgs.saveFailed'));
    } finally {
      setSaving(false);
    }
  };

  return (
    <Dialog open={open} onOpenChange={(_e, data) => !data.open && onDismiss()}>
      <DialogSurface mountNode={undefined}>
        <DialogBody>
          <DialogTitle>
            {editing
              ? t('userOrgs.dialog.editTitle', { name: editing.name })
              : t('userOrgs.dialog.newTitle')}
          </DialogTitle>
          <DialogContent>
            <div className={styles.form}>
              <Field
                label={t('userOrgs.dialog.nameLabel')}
                required
                hint={t('userOrgs.dialog.nameHint')}
              >
                <Input value={name} onChange={(_e, d) => setName(d.value)} maxLength={100} />
              </Field>

              <Field label={t('userOrgs.dialog.sourceLabel')}>
                <RadioGroup
                  value={source}
                  onChange={(_e, d) => {
                    setSource(d.value as UserOrgSource);
                    setTestResult(null);
                  }}
                >
                  <Radio value="entra" label={t('userOrgs.dialog.sourceEntra')} />
                  <Radio value="csv" label={t('userOrgs.dialog.sourceCsv')} />
                </RadioGroup>
              </Field>

              {editing &&
                editing.assignedUserCount > 0 &&
                (source !== editing.source ||
                  (source === 'entra' && attribute.trim() !== (editing.entraAttributeName ?? ''))) && (
                  <MessageBar intent="warning">
                    <MessageBarBody>
                      {t(
                        plural(
                          editing.assignedUserCount,
                          'userOrgs.dialog.discardWarning.one',
                          'userOrgs.dialog.discardWarning.other',
                        ),
                        { count: formatNumber(editing.assignedUserCount) },
                      )}
                    </MessageBarBody>
                  </MessageBar>
                )}

              {source === 'entra' && (
                <>
                  <Field
                    label={t('userOrgs.dialog.attributeLabel')}
                    required
                    hint={t('userOrgs.dialog.attributeHint')}
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
                    label={t('userOrgs.dialog.testLabel')}
                    hint={t('userOrgs.dialog.testHint')}
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
                        {testing ? t('userOrgs.dialog.testing') : t('userOrgs.dialog.testButton')}
                      </Button>
                    </div>
                  </Field>

                  {testResult && <TestOutcome result={testResult} styles={styles} t={t} />}

                  {!attributeProven && !testResult && (
                    <Text size={200} className={styles.muted}>
                      {t('userOrgs.dialog.testFirst')}
                    </Text>
                  )}
                </>
              )}

              <Switch
                checked={isEnabled}
                onChange={(_e, d) => setIsEnabled(d.checked)}
                label={isEnabled ? t('userOrgs.dialog.enabled') : t('userOrgs.dialog.disabled')}
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
              {t('userOrgs.dialog.cancel')}
            </Button>
            <Button appearance="primary" onClick={save} disabled={!canSave || saving}>
              {saving ? t('userOrgs.dialog.saving') : t('userOrgs.dialog.save')}
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
  t,
}: {
  result: UserOrgTestResult;
  styles: ReturnType<typeof useStyles>;
  t: TFunction;
}) {
  if (!result.succeeded) {
    return (
      <MessageBar intent="error">
        <MessageBarBody>{result.message ?? t('userOrgs.test.failed')}</MessageBarBody>
      </MessageBar>
    );
  }

  return (
    <div>
      <MessageBar intent={result.hasNoValue ? 'warning' : 'success'}>
        <MessageBarBody>{result.message ?? t('userOrgs.test.succeeded')}</MessageBarBody>
      </MessageBar>
      <div className={styles.resultGrid}>
        <Text className={styles.label}>{t('userOrgs.test.graphProperty')}</Text>
        <Text className={styles.mono}>{result.graphProperty ?? '—'}</Text>
        <Text className={styles.label}>{t('userOrgs.test.rawValue')}</Text>
        <Text className={styles.mono}>{result.rawValue ?? '—'}</Text>
        <Text className={styles.label}>{t('userOrgs.test.storedAs')}</Text>
        <Text className={styles.mono}>{result.normalisedValue ?? '—'}</Text>
      </div>
    </div>
  );
}
