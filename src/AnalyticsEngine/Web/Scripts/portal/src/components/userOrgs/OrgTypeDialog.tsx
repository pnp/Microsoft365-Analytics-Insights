import { useEffect, useMemo, useRef, useState } from 'react';
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
import CsvFileFormat from './CsvFileFormat';
import { userOrgErrorMessage, userOrgMessage } from './userOrgShared';
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
 * What two attribute names have in common when the server treats them as the same attribute: it
 * compares canonical names ignoring case (UserOrgAdminService.UpdateAsync), and the canonical form
 * drops the optional `onPremisesExtensionAttributes.` prefix and a slot's leading zero
 * (EntraOrgAttributeSpec.Canonical). Values are read ignoring case too, so names that differ only in
 * these ways read the same values - and saving one over the other discards nothing.
 */
function attributeKey(name: string | null | undefined): string {
  const key = (name ?? '').trim().toLowerCase();
  const slot = /^(?:onpremisesextensionattributes\.)?extensionattribute0*(\d+)$/.exec(key);
  return slot ? `extensionattribute${Number(slot[1])}` : key;
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

  // The attribute that was last proved to work, and the test that proved it - no test for the saved
  // attribute, which was proved when it was saved. Saving is gated on this matching what is in the box,
  // so editing the attribute after a successful test re-arms the requirement.
  const [proof, setProof] = useState<{ attribute: string; result: UserOrgTestResult | null } | null>(null);

  // Moves on every test, and every time the dialog opens, closes or turns to another type: a test answered
  // after that belongs to a dialog that is no longer there, and must not prove anything in this one.
  const testRun = useRef(0);

  useEffect(() => {
    testRun.current++;
    if (!open) return;
    setName(editing?.name ?? '');
    setSource(editing?.source ?? 'entra');
    setAttribute(editing?.entraAttributeName ?? '');
    setIsEnabled(editing?.isEnabled ?? true);
    setTesting(false);
    setTestResult(null);
    setSaveError(null);
    // An attribute that is already saved was proved when it was saved, so editing an unchanged type
    // does not force the admin to re-test it.
    setProof(editing?.entraAttributeName ? { attribute: editing.entraAttributeName, result: null } : null);
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

  // Whether saving throws away the values the type holds now: a change of source or attribute - by the
  // server's measure of "the same attribute", or the warning below cries wolf over a change of case.
  const wouldDiscard =
    !!editing &&
    editing.assignedUserCount > 0 &&
    (source !== editing.source ||
      (source === 'entra' && attributeKey(attribute) !== attributeKey(editing.entraAttributeName)));

  // A schema extension's property after the dot is not checked by Graph, so "this user has no value" is
  // also what a misspelt one looks like. Enough to set up a new type with; not enough to discard the
  // values an existing one holds on - that needs a test that finds a value.
  const unprovenForDiscard = (result: UserOrgTestResult | null) =>
    !!result && result.succeeded && result.hasNoValue && result.nameUnverified === true && wouldDiscard;

  // Judged against what saving would do now, not when the test ran.
  const attributeProven =
    !needsProof ||
    (proof !== null && attributeKey(proof.attribute) === attributeKey(attribute) && !unprovenForDiscard(proof.result));

  const canSave =
    name.trim().length > 0 &&
    (source !== 'entra' || attribute.trim().length > 0) &&
    attributeProven;

  const runTest = async () => {
    const run = ++testRun.current;
    setTesting(true);
    setTestResult(null);
    try {
      const result = await testEntraAttribute(attribute, testUpn);
      if (run !== testRun.current) return;
      setTestResult(result);
      // A user who simply has no value still proves the attribute is readable, which is what saving
      // is gated on - the attribute existing, not this particular person having a value for it. Except
      // where Graph never checked the name and the save would discard values: see unprovenForDiscard.
      setProof(result.succeeded ? { attribute, result } : null);
    } catch (e) {
      if (run !== testRun.current) return;
      setTestResult({
        succeeded: false,
        upn: testUpn,
        attributeName: attribute,
        graphProperty: null,
        rawValue: null,
        normalisedValue: null,
        wouldTruncate: false,
        hasNoValue: false,
        message: userOrgErrorMessage(e, t, 'errors.userOrgs.testFailed'),
      });
      setProof(null);
    } finally {
      if (run === testRun.current) setTesting(false);
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
      setSaveError(userOrgErrorMessage(e, t, 'errors.userOrgs.saveFailed'));
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

              {wouldDiscard && (
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
                      // Fluent clears the selection as soon as the text stops matching the selected option,
                      // and reports that as a selection of nothing - after onInput, so taking it at its word
                      // emptied the field on the first keystroke or paste over an existing type's attribute.
                      // The text arrives through onInput; only a real pick from the list replaces it.
                      onOptionSelect={(_e, d) => {
                        if (d.optionValue !== undefined) setAttribute(d.optionValue);
                      }}
                      placeholder="extensionAttribute1"
                      maxLength={200}
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
                      <MessageBarBody>
                        {userOrgMessage(
                          catalogue.discoveryWarningCode,
                          catalogue.discoveryWarningValues,
                          catalogue.discoveryWarning,
                          t,
                        )}
                      </MessageBarBody>
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

                  {testResult && (
                    <TestOutcome
                      result={testResult}
                      styles={styles}
                      t={t}
                      blocksSave={unprovenForDiscard(testResult)}
                    />
                  )}

                  {!attributeProven && !testResult && (
                    <Text size={200} className={styles.muted}>
                      {t('userOrgs.dialog.testFirst')}
                    </Text>
                  )}
                </>
              )}

              {/* What to produce before the upload card appears: the type has to exist first, and an
                  admin choosing "CSV" is deciding what file to go and generate. */}
              {source === 'csv' && <CsvFileFormat typeName={name} />}

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
  blocksSave,
}: {
  result: UserOrgTestResult;
  styles: ReturnType<typeof useStyles>;
  t: TFunction;
  /** The test cannot vouch for the name, and the save would discard values on it. */
  blocksSave: boolean;
}) {
  if (!result.succeeded) {
    return (
      <MessageBar intent="error">
        <MessageBarBody>
          {userOrgMessage(result.messageCode, result.messageValues, result.message, t) ?? t('userOrgs.test.failed')}
        </MessageBarBody>
      </MessageBar>
    );
  }

  return (
    <div>
      <MessageBar intent={result.hasNoValue ? 'warning' : 'success'}>
        <MessageBarBody>
          {userOrgMessage(result.messageCode, result.messageValues, result.message, t) ?? t('userOrgs.test.succeeded')}
          {blocksSave && <> {t('userOrgs.test.unverifiedBeforeDiscard')}</>}
        </MessageBarBody>
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
