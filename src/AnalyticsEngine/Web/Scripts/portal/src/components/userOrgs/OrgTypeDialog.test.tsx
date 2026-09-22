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
  assignedUserCount: 10,
  distinctValueCount: 4,
  createdUtc: '2026-01-01T00:00:00.000Z',
  modifiedUtc: null,
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
    // test proves both are rendered rather than one value appearing twice.
    const raw = 'x'.repeat(210);
    const stored = 'x'.repeat(200);

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

  it('does not force a re-test when editing an existing type whose attribute is unchanged', async () => {
    renderWithProvider(<OrgTypeDialog open editing={saved} onDismiss={vi.fn()} onSave={vi.fn()} />);

    await waitFor(() => expect(screen.getByRole('button', { name: 'Save' })).toBeEnabled());
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
});
