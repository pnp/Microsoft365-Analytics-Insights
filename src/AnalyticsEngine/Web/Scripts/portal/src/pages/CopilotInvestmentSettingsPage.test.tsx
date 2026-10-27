import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, screen } from '@testing-library/react';

import { renderWithProvider } from '../test/renderWithProvider';
import CopilotInvestmentSettingsPage from './CopilotInvestmentSettingsPage';
import {
  InvestmentSettingsError,
  fetchInvestmentSettings,
  saveInvestmentSettings,
  type InvestmentSettingsModel,
} from '../api/copilotInvestmentApi';

vi.mock('../api/copilotInvestmentApi', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../api/copilotInvestmentApi')>()),
  fetchInvestmentSettings: vi.fn(),
  saveInvestmentSettings: vi.fn(),
}));

/** Nothing set yet: the report must not model money with figures nobody chose. */
const neverSaved: InvestmentSettingsModel = {
  durable: true,
  version: 0,
  assumptions: {
    currency: null,
    seatPricePerMonth: null,
    creditPrice: null,
    studioCreditPrice: null,
    hourlyCost: null,
    minutesPerActiveDay: { conservative: null, base: null, upside: null },
    realisation: { conservative: null, base: null, upside: null },
    note: null,
  },
  updatedBy: null,
  updatedUtc: null,
  history: [],
};

beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(fetchInvestmentSettings).mockResolvedValue(neverSaved);
});

describe('CopilotInvestmentSettingsPage', () => {
  it('saves what the administrator typed, decimal commas included, against the version they loaded', async () => {
    vi.mocked(saveInvestmentSettings).mockImplementation(async (_version, assumptions) => ({
      ...neverSaved,
      version: 1,
      assumptions,
      updatedBy: 'admin@contoso.example',
      updatedUtc: '2026-10-27T10:00:00Z',
    }));
    renderWithProvider(<CopilotInvestmentSettingsPage />);

    fireEvent.change(await screen.findByLabelText('Currency'), { target: { value: 'eur' } });
    fireEvent.change(screen.getByLabelText('Licence price a month'), { target: { value: '30' } });
    fireEvent.change(screen.getByLabelText('Fully loaded hourly cost'), { target: { value: '52,5' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save assumptions' }));

    expect(await screen.findByText('Assumptions saved (version 1).')).toBeInTheDocument();
    const [expectedVersion, sent] = vi.mocked(saveInvestmentSettings).mock.calls[0];
    expect(expectedVersion).toBe(0);
    expect(sent.currency).toBe('EUR');
    expect(sent.seatPricePerMonth).toBe(30);
    expect(sent.hourlyCost).toBe(52.5);
    // Left empty, so still missing - never defaulted to a price nobody chose.
    expect(sent.creditPrice).toBeNull();
    expect(sent.minutesPerActiveDay.base).toBeNull();
  });

  it('refuses text that is not a number without sending anything', async () => {
    renderWithProvider(<CopilotInvestmentSettingsPage />);

    fireEvent.change(await screen.findByLabelText('Licence price a month'), { target: { value: 'thirty' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save assumptions' }));

    expect(await screen.findByText('Enter a number, using a full stop or comma for decimals.')).toBeInTheDocument();
    expect(saveInvestmentSettings).not.toHaveBeenCalled();
  });

  it('explains a version conflict and each rule the server applied', async () => {
    vi.mocked(saveInvestmentSettings).mockRejectedValueOnce(new InvestmentSettingsError('versionConflict', []));
    renderWithProvider(<CopilotInvestmentSettingsPage />);

    fireEvent.change(await screen.findByLabelText('Licence price a month'), { target: { value: '30' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save assumptions' }));
    expect(await screen.findByText(/Someone else saved the assumptions while you were editing/)).toBeInTheDocument();

    vi.mocked(saveInvestmentSettings).mockRejectedValueOnce(new InvestmentSettingsError('invalidRequest', ['currencyRequired', 'minutesOrder']));
    fireEvent.click(screen.getByRole('button', { name: 'Save assumptions' }));
    expect(await screen.findByText('Set a currency for the prices and the hourly cost.')).toBeInTheDocument();
    expect(screen.getByText('Minutes must run from conservative to upside without going down.')).toBeInTheDocument();
    expect(saveInvestmentSettings).toHaveBeenCalledTimes(2);
  });

  it('does not offer to save where nothing can be kept', async () => {
    vi.mocked(fetchInvestmentSettings).mockResolvedValue({ ...neverSaved, durable: false });
    renderWithProvider(<CopilotInvestmentSettingsPage />);

    expect(await screen.findByLabelText('Licence price a month')).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Save assumptions' })).toBeDisabled();
  });
});
