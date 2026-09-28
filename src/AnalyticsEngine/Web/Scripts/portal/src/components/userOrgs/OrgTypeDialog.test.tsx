import { describe, it, expect, vi, beforeEach } from 'vitest';
import { screen, waitFor, fireEvent } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { renderWithProvider } from '../../test/renderWithProvider';
import OrgTypeDialog from './OrgTypeDialog';
import type { UserOrgTestResult, UserOrgType } from '../../types/userOrgs';

const fetchAttributeCatalogue = vi.fn();
const testEntraAttribute = vi.fn();

vi.mock('../../api/userOrgsApi', () => ({
  fetchAttributeCatalogue: () => fetchAttributeCatalogue(),
  testEntraAttribute: (...args: unknown[]) => testEntraAttribute(...args),
}));

function catalogue(over: Record<string, unknown> = {}) {
  return {
    extensionAttributes: ['extensionAttribute1', 'extensionAttribute2'],
    builtInProperties: ['employeeId', 'employeeType'],
    employeeOrgDataProperties: ['employeeOrgData.costCenter'],
    directoryExtensions: [],
    discoveryWarning: null,
    ...over,
  };
}

function testResult(over: Partial<UserOrgTestResult> = {}): UserOrgTestResult {
  return {
    succeeded: true,
    upn: 'someone@contoso.com',
    attributeName: 'extensionAttribute1',
    graphProperty: 'onPremisesExtensionAttributes',
    rawValue: 'CC-1042',
    normalisedValue: 'CC-1042',
    wouldTruncate: false,
    hasNoValue: false,
    message: null,
    ...over,
  };
}

const saved: UserOrgType = {
  id: 3,
  name: 'Cost Centre',
  source: 'entra',
  entraAttributeName: 'extensionAttribute1',
  isEnabled: true,
  revision: 1,
  assignedUserCount: 10,
  distinctValueCount: 4,
  createdUtc: '2026-01-01T00:00:00.000Z',
  modifiedUtc: null,
  lastRefreshedUtc: null,
  lastImport: null,
};

async function typeInto(label: RegExp, value: string) {
  // Not anchored with $: Fluent's Field appends a required marker to the label text, so an exact
  // match would never find a required field.
  const input = screen.getByLabelText(label);
  await userEvent.clear(input);
  await userEvent.type(input, value);
}

/**
 * The attribute field is a Combobox, which labels both its input and its listbox - so getByLabelText
 * finds two elements. Selecting by role picks the input unambiguously, and firing `input` directly
 * matches the handler the component actually listens on (a freeform Combobox does not emit the
 * change events userEvent.type would rely on).
 */
function typeAttribute(value: string) {
  const input = screen.getByRole('combobox', { name: /Entra attribute/ });
  fireEvent.input(input, { target: { value } });
}

describe('OrgTypeDialog', () => {
  beforeEach(() => {
    fetchAttributeCatalogue.mockReset();
    testEntraAttribute.mockReset();
    fetchAttributeCatalogue.mockResolvedValue(catalogue());
  });

  it('will not save a new Entra type until the attribute has been proved against a user', async () => {
    // This is the guard that stops a typo reaching the importer: Graph fails the entire
    // /users/delta request when $select names a property it does not recognise.
    const onSave = vi.fn().mockResolvedValue(undefined);
    renderWithProvider(<OrgTypeDialog open editing={null} onDismiss={vi.fn()} onSave={onSave} />);

    await typeInto(/^Name/, 'Cost Centre');
    typeAttribute('extensionAttribute1');

    expect(screen.getByRole('button', { name: 'Save' })).toBeDisabled();
    expect(screen.getByText(/Test the attribute against a user before saving/i)).toBeInTheDocument();

    testEntraAttribute.mockResolvedValue(testResult());
    await typeInto(/Test it against a user/, 'someone@contoso.com');
    await userEvent.click(screen.getByRole('button', { name: 'Test' }));

    await waitFor(() => expect(screen.getByRole('button', { name: 'Save' })).toBeEnabled());
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));

    expect(onSave).toHaveBeenCalledWith(
      expect.objectContaining({ name: 'Cost Centre', source: 'entra', entraAttributeName: 'extensionAttribute1' }),
    );
  });

  it('shows the raw and stored values so trimming and truncation are visible', async () => {
    // Deliberately a value long enough to be shortened, so the raw and stored columns differ and the
    // test proves both are rendered rather than one value appearing twice. Stored at the real limit.
    const raw = 'x'.repeat(860);
    const stored = 'x'.repeat(848);

    renderWithProvider(<OrgTypeDialog open editing={null} onDismiss={vi.fn()} onSave={vi.fn()} />);

    await typeInto(/^Name/, 'Cost Centre');
    typeAttribute('extensionAttribute1');
    testEntraAttribute.mockResolvedValue(
      testResult({ rawValue: raw, normalisedValue: stored, wouldTruncate: true, message: 'too long' }),
    );
    await typeInto(/Test it against a user/, 'someone@contoso.com');
    await userEvent.click(screen.getByRole('button', { name: 'Test' }));

    await waitFor(() => expect(screen.getByText('Graph property')).toBeInTheDocument());
    expect(screen.getByText('onPremisesExtensionAttributes')).toBeInTheDocument();
    expect(screen.getByText(raw)).toBeInTheDocument();
    expect(screen.getByText(stored)).toBeInTheDocument();
  });

  it('keeps saving enabled when the user simply has no value for the attribute', async () => {
    // The gate is "is this attribute readable", not "does this particular person have a value".
    const onSave = vi.fn().mockResolvedValue(undefined);
    renderWithProvider(<OrgTypeDialog open editing={null} onDismiss={vi.fn()} onSave={onSave} />);

    await typeInto(/^Name/, 'Cost Centre');
    typeAttribute('extensionAttribute1');
    testEntraAttribute.mockResolvedValue(
      testResult({ rawValue: null, normalisedValue: null, hasNoValue: true, message: 'no value' }),
    );
    await typeInto(/Test it against a user/, 'someone@contoso.com');
    await userEvent.click(screen.getByRole('button', { name: 'Test' }));

    await waitFor(() => expect(screen.getByRole('button', { name: 'Save' })).toBeEnabled());
  });

  it('will not discard a type\u2019s values on a schema extension property Graph never checked', async () => {
    // Graph selects only the part before the dot, so a misspelt property after it reads as "this user
    // has no value". Enough for a new type; not enough to throw away the values an existing one holds.
    const schemaNoValue = testResult({
      attributeName: 'contoso_costs.costCentre',
      graphProperty: 'contoso_costs',
      rawValue: null,
      normalisedValue: null,
      hasNoValue: true,
      nameUnverified: true,
      message: 'no value',
      messageCode: 'noValueUnverified',
      messageValues: { container: 'contoso_costs' },
    });
    renderWithProvider(
      <OrgTypeDialog
        open
        editing={{ ...saved, source: 'csv', entraAttributeName: null }}
        onDismiss={vi.fn()}
        onSave={vi.fn()}
      />,
    );

    // Switching a CSV type with values to an Entra attribute discards them.
    await userEvent.click(screen.getByLabelText('A custom Microsoft Entra attribute, read on every user import'));
    typeAttribute('contoso_costs.costCentre');
    testEntraAttribute.mockResolvedValue(schemaNoValue);
    await typeInto(/Test it against a user/, 'someone@contoso.com');
    await userEvent.click(screen.getByRole('button', { name: 'Test' }));

    expect(await screen.findByText(/Microsoft Graph accepted the schema extension 'contoso_costs'/)).toBeInTheDocument();
    expect(screen.getByText(/so it can only be saved once a test finds a value/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Save' })).toBeDisabled();

    testEntraAttribute.mockResolvedValue({ ...schemaNoValue, rawValue: 'CC-1042', normalisedValue: 'CC-1042', hasNoValue: false, messageCode: null });
    await userEvent.click(screen.getByRole('button', { name: 'Test' }));

    await waitFor(() => expect(screen.getByRole('button', { name: 'Save' })).toBeEnabled());
  });

  it('lets a new type rest on a schema extension test that found no value, and says what that cannot show', async () => {
    renderWithProvider(<OrgTypeDialog open editing={null} onDismiss={vi.fn()} onSave={vi.fn()} />);

    await typeInto(/^Name/, 'Cost Centre');
    typeAttribute('contoso_costs.costCentre');
    testEntraAttribute.mockResolvedValue(
      testResult({
        attributeName: 'contoso_costs.costCentre',
        graphProperty: 'contoso_costs',
        rawValue: null,
        normalisedValue: null,
        hasNoValue: true,
        nameUnverified: true,
        messageCode: 'noValueUnverified',
        messageValues: { container: 'contoso_costs' },
      }),
    );
    await typeInto(/Test it against a user/, 'someone@contoso.com');
    await userEvent.click(screen.getByRole('button', { name: 'Test' }));

    await waitFor(() => expect(screen.getByRole('button', { name: 'Save' })).toBeEnabled());
    expect(screen.getByText(/does not check the property name after the dot/)).toBeInTheDocument();
    expect(screen.queryByText(/can only be saved once a test finds a value/)).not.toBeInTheDocument();
  });

  it('keeps saving disabled when the attribute cannot be read, and explains why', async () => {
    renderWithProvider(<OrgTypeDialog open editing={null} onDismiss={vi.fn()} onSave={vi.fn()} />);

    await typeInto(/^Name/, 'Cost Centre');
    typeAttribute('extension_bad');
    testEntraAttribute.mockResolvedValue(
      testResult({ succeeded: false, message: 'Microsoft Graph does not recognise the property.' }),
    );
    await typeInto(/Test it against a user/, 'someone@contoso.com');
    await userEvent.click(screen.getByRole('button', { name: 'Test' }));

    await waitFor(() =>
      expect(screen.getByText(/does not recognise the property/)).toBeInTheDocument(),
    );
    expect(screen.getByRole('button', { name: 'Save' })).toBeDisabled();
  });

  it('re-arms the test requirement when the attribute is edited after a successful test', async () => {
    renderWithProvider(<OrgTypeDialog open editing={null} onDismiss={vi.fn()} onSave={vi.fn()} />);

    await typeInto(/^Name/, 'Cost Centre');
    typeAttribute('extensionAttribute1');
    testEntraAttribute.mockResolvedValue(testResult());
    await typeInto(/Test it against a user/, 'someone@contoso.com');
    await userEvent.click(screen.getByRole('button', { name: 'Test' }));
    await waitFor(() => expect(screen.getByRole('button', { name: 'Save' })).toBeEnabled());

    typeAttribute('extensionAttribute2');

    expect(screen.getByRole('button', { name: 'Save' })).toBeDisabled();
  });

  it('does not require a test for a CSV-sourced type', async () => {
    const onSave = vi.fn().mockResolvedValue(undefined);
    renderWithProvider(<OrgTypeDialog open editing={null} onDismiss={vi.fn()} onSave={onSave} />);

    await typeInto(/^Name/, 'From spreadsheet');
    await userEvent.click(screen.getByLabelText(/A CSV file uploaded here/));

    await waitFor(() => expect(screen.getByRole('button', { name: 'Save' })).toBeEnabled());
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));

    expect(onSave).toHaveBeenCalledWith(
      expect.objectContaining({ source: 'csv', entraAttributeName: null }),
    );
  });

  it('lets a broken Entra type be disabled without proving its attribute first', async () => {
    // The recovery path. When a directory extension is deleted from the tenant, the import tells the
    // admin to fix the type here - and turning it off is the only fix that keeps the values, since
    // deleting, repointing or switching to CSV all discard them. Demanding a successful test first
    // would leave them with no non-destructive option at all.
    testEntraAttribute.mockResolvedValue(
      testResult({ succeeded: false, message: 'Microsoft Graph does not recognise the property.' }),
    );
    const onSave = vi.fn().mockResolvedValue(undefined);
    renderWithProvider(<OrgTypeDialog open editing={null} onDismiss={vi.fn()} onSave={onSave} />);

    await typeInto(/^Name/, 'Cost Centre');
    typeAttribute('extension_00000000000000000000000000000000_gone');

    // Enabled and unproven: refused, which is the behaviour a live type must keep.
    await waitFor(() => expect(screen.getByRole('button', { name: 'Save' })).toBeDisabled());

    fireEvent.click(screen.getByRole('switch'));

    await waitFor(() => expect(screen.getByRole('button', { name: 'Save' })).toBeEnabled());
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));

    expect(onSave).toHaveBeenCalledWith(expect.objectContaining({ isEnabled: false }));
  });

  it('does not force a re-test when editing an existing type whose attribute is unchanged', async () => {
    renderWithProvider(<OrgTypeDialog open editing={saved} onDismiss={vi.fn()} onSave={vi.fn()} />);

    await waitFor(() => expect(screen.getByRole('button', { name: 'Save' })).toBeEnabled());
  });

  it('keeps what the admin types over an existing type\u2019s attribute', async () => {
    // Fluent clears the Combobox selection once the text stops matching it, reported as a selection of
    // nothing. Taken at its word, the first keystroke or paste over a saved attribute emptied the field.
    renderWithProvider(<OrgTypeDialog open editing={saved} onDismiss={vi.fn()} onSave={vi.fn()} />);

    typeAttribute('contoso_costs.costCentre');
    await typeInto(/Test it against a user/, 'someone@contoso.com');

    expect(screen.getByRole('combobox', { name: /Entra attribute/ })).toHaveValue('contoso_costs.costCentre');
    expect(screen.getByRole('button', { name: 'Test' })).toBeEnabled();
    expect(screen.getByRole('button', { name: 'Save' })).toBeDisabled();
  });

  it('still takes an attribute picked from the list', async () => {
    renderWithProvider(<OrgTypeDialog open editing={saved} onDismiss={vi.fn()} onSave={vi.fn()} />);

    await userEvent.click(screen.getByRole('combobox', { name: /Entra attribute/ }));
    await userEvent.click(await screen.findByRole('option', { name: 'employeeType' }));

    expect(screen.getByRole('combobox', { name: /Entra attribute/ })).toHaveValue('employeeType');
    expect(screen.getByRole('button', { name: 'Save' })).toBeDisabled();
  });

  it('treats a change of case alone as the same attribute, as the server does', async () => {
    // The server compares attribute names ignoring case and reads values ignoring case, so this save
    // keeps every value. Warning that it discards them - or refusing it on a test that found no value -
    // would stop an admin making a harmless correction.
    renderWithProvider(
      <OrgTypeDialog
        open
        editing={{ ...saved, entraAttributeName: 'contoso_costs.costCentre' }}
        onDismiss={vi.fn()}
        onSave={vi.fn()}
      />,
    );

    typeAttribute('contoso_costs.costcentre');
    testEntraAttribute.mockResolvedValue(
      testResult({
        attributeName: 'contoso_costs.costcentre',
        graphProperty: 'contoso_costs',
        rawValue: null,
        normalisedValue: null,
        hasNoValue: true,
        nameUnverified: true,
        messageCode: 'noValueUnverified',
        messageValues: { container: 'contoso_costs' },
      }),
    );
    await typeInto(/Test it against a user/, 'someone@contoso.com');
    await userEvent.click(screen.getByRole('button', { name: 'Test' }));

    expect(await screen.findByText(/does not check the property name after the dot/)).toBeInTheDocument();
    expect(screen.queryByText(/Saving this discards/)).not.toBeInTheDocument();
    expect(screen.queryByText(/can only be saved once a test finds a value/)).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Save' })).toBeEnabled();
  });

  it('treats the long and zero-padded spellings of an extension attribute slot as the same slot', async () => {
    // Both are stored as extensionAttribute1 (EntraOrgAttributeSpec.Canonical): nothing to re-prove,
    // nothing discarded. A different slot still is.
    renderWithProvider(<OrgTypeDialog open editing={saved} onDismiss={vi.fn()} onSave={vi.fn()} />);

    typeAttribute('onPremisesExtensionAttributes.extensionAttribute01');

    expect(screen.queryByText(/Saving this discards/)).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Save' })).toBeEnabled();

    typeAttribute('extensionAttribute2');

    expect(screen.getByText(/Saving this discards the 10 values this type holds today/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Save' })).toBeDisabled();
  });

  it('surfaces the discovery warning rather than presenting an empty list as "none exist"', async () => {
    fetchAttributeCatalogue.mockResolvedValue(
      catalogue({ discoveryWarning: 'No directory extensions were returned.' }),
    );
    renderWithProvider(<OrgTypeDialog open editing={null} onDismiss={vi.fn()} onSave={vi.fn()} />);

    await waitFor(() =>
      expect(screen.getByText(/No directory extensions were returned/)).toBeInTheDocument(),
    );
  });

  it('words a coded discovery warning in the reader\u2019s language, not the server\u2019s English', async () => {
    fetchAttributeCatalogue.mockResolvedValue(
      catalogue({
        discoveryWarning: 'Microsoft Graph could not list directory extensions (HTTP 503).',
        discoveryWarningCode: 'discoveryGraphError',
        discoveryWarningValues: { status: 503 },
      }),
    );
    renderWithProvider(<OrgTypeDialog open editing={null} onDismiss={vi.fn()} onSave={vi.fn()} />, { language: 'es' });

    expect(await screen.findByText(/no pudo enumerar las extensiones de directorio \(HTTP 503\)/)).toBeInTheDocument();
    expect(screen.queryByText(/could not list directory extensions/)).not.toBeInTheDocument();
  });

  it('words a coded test outcome, with its facts, in the reader\u2019s language', async () => {
    renderWithProvider(<OrgTypeDialog open editing={null} onDismiss={vi.fn()} onSave={vi.fn()} />, { language: 'es' });

    fireEvent.input(screen.getByRole('combobox', { name: /Atributo de Entra/ }), {
      target: { value: 'extension_00000000000000000000000000000000_skills' },
    });
    testEntraAttribute.mockResolvedValue(
      testResult({
        succeeded: false,
        message: 'server English fallback',
        messageCode: 'multiValued',
        messageValues: { property: 'extension_00000000000000000000000000000000_skills' },
      }),
    );
    await typeInto(/Pru\u00e9belo con un usuario/, 'someone@contoso.com');
    await userEvent.click(screen.getByRole('button', { name: 'Probar' }));

    expect(
      await screen.findByText(/\u00abextension_00000000000000000000000000000000_skills\u00bb contiene una lista de valores/),
    ).toBeInTheDocument();
    expect(screen.queryByText('server English fallback')).not.toBeInTheDocument();
  });

  it('words a coded save refusal, and falls back to the server\u2019s text for a code it does not know', async () => {
    const known = Object.assign(new Error('An organisation type called \u2018x\u2019 already exists.'), {
      code: 'duplicateName',
      values: { name: 'Cost Centre' },
    });
    const unknown = Object.assign(new Error('Something only a newer server knows how to say.'), {
      code: 'somethingNew',
      values: {},
    });
    const onSave = vi.fn().mockRejectedValueOnce(known).mockRejectedValueOnce(unknown);
    renderWithProvider(<OrgTypeDialog open editing={null} onDismiss={vi.fn()} onSave={onSave} />);

    await typeInto(/^Name/, 'Cost Centre');
    await userEvent.click(screen.getByLabelText(/A CSV file uploaded here/));
    await userEvent.click(await screen.findByRole('button', { name: 'Save' }));
    expect(await screen.findByText('An organisation type called \u201cCost Centre\u201d already exists.')).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Save' }));
    expect(await screen.findByText('Something only a newer server knows how to say.')).toBeInTheDocument();
  });
});
