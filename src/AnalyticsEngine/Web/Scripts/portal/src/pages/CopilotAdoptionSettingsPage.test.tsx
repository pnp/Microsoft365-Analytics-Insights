import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import { renderWithProvider } from '../test/renderWithProvider';
import { loadCatalog } from '../i18n';
import CopilotAdoptionSettingsPage from './CopilotAdoptionSettingsPage';
import { CopilotAdoptionSettingsError } from '../api/copilotAdoptionSettingsApi';
import { PortalPermissionError } from '../api/http';
import { validateScoreSettings } from '../components/copilotAdoption/scoreSettings';
import type { CopilotAdoptionScoreValues, CopilotAdoptionSettingsModel } from '../types/copilotAdoptionSettings';

const mockFetch = vi.fn();
const mockSave = vi.fn();
const mockReset = vi.fn();

vi.mock('../api/copilotAdoptionSettingsApi', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../api/copilotAdoptionSettingsApi')>()),
  fetchCopilotAdoptionSettings: (...args: unknown[]) => mockFetch(...args),
  saveCopilotAdoptionSettings: (...args: unknown[]) => mockSave(...args),
  resetCopilotAdoptionSettings: (...args: unknown[]) => mockReset(...args),
}));

const DEFAULTS: CopilotAdoptionScoreValues = {
  frequencyWeightPercent: 50,
  depthWeightPercent: 30,
  breadthWeightPercent: 20,
  developingScore: 25,
  establishedScore: 50,
  championScore: 75,
};

const model = (over: Partial<CopilotAdoptionSettingsModel> = {}): CopilotAdoptionSettingsModel => ({
  durable: true,
  version: 0,
  settings: { ...DEFAULTS },
  defaults: { ...DEFAULTS },
  customisedFields: [],
  updatedBy: null,
  updatedUtc: null,
  history: [],
  propagationSeconds: 15,
  minThreshold: 1,
  maxThreshold: 100,
  ...over,
});

const CUSTOMISED = model({
  version: 3,
  settings: { ...DEFAULTS, frequencyWeightPercent: 60, depthWeightPercent: 20, championScore: 90 },
  customisedFields: ['frequencyWeightPercent', 'depthWeightPercent', 'championScore'],
  updatedBy: 'Νίκος@contoso.com',
  updatedUtc: '2026-10-01T09:00:00Z',
  history: [{
    version: 3,
    action: 'save',
    changedBy: 'Νίκος@contoso.com',
    changedUtc: '2026-10-01T09:00:00Z',
    changes: [{ field: 'championScore', oldValue: 75, newValue: 90 }],
  }],
});

function field(label: string): HTMLInputElement {
  return screen.getByLabelText(label) as HTMLInputElement;
}

beforeEach(() => {
  vi.clearAllMocks();
  mockFetch.mockResolvedValue(model());
});

describe('validateScoreSettings (mirrors the server rules)', () => {
  it('accepts the defaults and the edge values', () => {
    expect(validateScoreSettings(DEFAULTS)).toEqual([]);
    expect(validateScoreSettings({ ...DEFAULTS, frequencyWeightPercent: 100, depthWeightPercent: 0, breadthWeightPercent: 0, developingScore: 1, establishedScore: 2, championScore: 100 })).toEqual([]);
  });

  it('rejects weights out of range or not totalling 100', () => {
    expect(validateScoreSettings({ ...DEFAULTS, frequencyWeightPercent: -1, depthWeightPercent: 81 })).toEqual(['weightOutOfRange']);
    expect(validateScoreSettings({ ...DEFAULTS, frequencyWeightPercent: 50.5 })).toEqual(['weightOutOfRange']);
    expect(validateScoreSettings({ ...DEFAULTS, frequencyWeightPercent: 60 })).toEqual(['weightsMustTotal100']);
  });

  it('rejects thresholds out of range or not strictly ascending', () => {
    expect(validateScoreSettings({ ...DEFAULTS, developingScore: 0 })).toEqual(['thresholdOutOfRange']);
    expect(validateScoreSettings({ ...DEFAULTS, championScore: 101 })).toEqual(['thresholdOutOfRange']);
    expect(validateScoreSettings({ ...DEFAULTS, establishedScore: 75 })).toEqual(['thresholdsNotAscending']);
    expect(validateScoreSettings({ ...DEFAULTS, developingScore: 60 })).toEqual(['thresholdsNotAscending']);
  });
});

describe('CopilotAdoptionSettingsPage', () => {
  it('shows the defaults, with nothing to save or reset', async () => {
    renderWithProvider(<CopilotAdoptionSettingsPage />);

    expect(await screen.findByText('Copilot Adoption score settings')).toBeVisible();
    expect(screen.getByText('Using defaults')).toBeVisible();
    expect(field('Frequency weight (%)').value).toBe('50');
    expect(field('Champion from score').value).toBe('75');
    expect(screen.getByText('Total: 100%')).toBeVisible();
    expect(screen.getByText('These settings have never been changed.')).toBeVisible();
    expect(screen.getByRole('button', { name: 'Save' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Reset to defaults' })).toBeDisabled();
  });

  it('keeps the live total and refuses weights that do not add up to 100', async () => {
    renderWithProvider(<CopilotAdoptionSettingsPage />);
    await screen.findByText('Using defaults');

    fireEvent.change(field('Frequency weight (%)'), { target: { value: '60' } });

    expect(screen.getByText('Total: 110%')).toBeVisible();
    expect(screen.getByText('The three weights must add up to 100.')).toBeVisible();
    expect(screen.getByRole('button', { name: 'Save' })).toBeDisabled();
  });

  it('refuses thresholds that do not increase', async () => {
    renderWithProvider(<CopilotAdoptionSettingsPage />);
    await screen.findByText('Using defaults');

    fireEvent.change(field('Established from score'), { target: { value: '80' } });

    expect(screen.getByText(/The thresholds must increase/)).toBeVisible();
    expect(screen.getByRole('button', { name: 'Save' })).toBeDisabled();
  });

  it('saves a valid change with the version it was read at', async () => {
    mockSave.mockResolvedValue(CUSTOMISED);
    renderWithProvider(<CopilotAdoptionSettingsPage />);
    await screen.findByText('Using defaults');

    fireEvent.change(field('Frequency weight (%)'), { target: { value: '60' } });
    fireEvent.change(field('Depth weight (%)'), { target: { value: '20' } });
    fireEvent.change(field('Champion from score'), { target: { value: '90' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() => expect(mockSave).toHaveBeenCalledWith(0, {
      ...DEFAULTS, frequencyWeightPercent: 60, depthWeightPercent: 20, championScore: 90,
    }));
    expect(await screen.findByText('Settings saved. Other web servers will use them within 15 seconds.')).toBeVisible();
    expect(screen.getByText('Customised')).toBeVisible();
    expect(screen.getByText('Champion from score: 75 → 90')).toBeVisible();
  });

  it('explains a conflicting save by another administrator', async () => {
    mockSave.mockRejectedValue(new CopilotAdoptionSettingsError('settingsChanged', []));
    renderWithProvider(<CopilotAdoptionSettingsPage />);
    await screen.findByText('Using defaults');

    fireEvent.change(field('Developing from score'), { target: { value: '30' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));

    expect(await screen.findByText(/Another administrator changed these settings/)).toBeVisible();
  });

  it('reports every rule the server rejected', async () => {
    mockSave.mockRejectedValue(new CopilotAdoptionSettingsError('weightsMustTotal100', ['weightsMustTotal100', 'thresholdsNotAscending']));
    renderWithProvider(<CopilotAdoptionSettingsPage />);
    await screen.findByText('Using defaults');

    fireEvent.change(field('Developing from score'), { target: { value: '30' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));

    const message = await screen.findByText(/The three weights must add up to 100\./);
    expect(message.textContent).toContain('The thresholds must increase');
  });

  it('shows who changed what, and resets to the defaults', async () => {
    mockFetch.mockResolvedValue(CUSTOMISED);
    mockReset.mockResolvedValue(model({ version: 4, history: [{ version: 4, action: 'reset', changedBy: 'admin@contoso.com', changedUtc: '2026-10-02T09:00:00Z', changes: [] }, ...CUSTOMISED.history] }));
    renderWithProvider(<CopilotAdoptionSettingsPage />);

    expect(await screen.findByText('Customised')).toBeVisible();
    expect(screen.getByText(/Last changed by Νίκος@contoso.com/)).toBeVisible();
    expect(screen.getAllByText('Νίκος@contoso.com').length).toBeGreaterThan(0);
    expect(screen.getByText('Champion from score: 75 → 90')).toBeVisible();

    fireEvent.click(screen.getByRole('button', { name: 'Reset to defaults' }));

    await waitFor(() => expect(mockReset).toHaveBeenCalledWith(3));
    expect(await screen.findByText(/The default settings have been restored/)).toBeVisible();
    expect(screen.getByText('Using defaults')).toBeVisible();
    expect(field('Champion from score').value).toBe('75');
  });

  it('cannot save without durable storage, and says why', async () => {
    mockFetch.mockResolvedValue(model({ durable: false }));
    renderWithProvider(<CopilotAdoptionSettingsPage />);
    await screen.findByText('Using defaults');

    expect(screen.getByText(/no Azure Storage connection string is configured/)).toBeVisible();
    fireEvent.change(field('Developing from score'), { target: { value: '30' } });
    expect(screen.getByRole('button', { name: 'Save' })).toBeDisabled();
  });

  it('explains a refused or failed load', async () => {
    const refusal = new PortalPermissionError('administration', 'Portal.Administration');
    mockFetch.mockRejectedValue(refusal);
    renderWithProvider(<CopilotAdoptionSettingsPage />);
    expect(await screen.findByText(refusal.message)).toBeVisible();
  });

  it('explains an unreachable settings store', async () => {
    mockFetch.mockRejectedValue(new CopilotAdoptionSettingsError('stateUnavailable', []));
    renderWithProvider(<CopilotAdoptionSettingsPage />);
    expect(await screen.findByText(/could not be reached/)).toBeVisible();
  });

  it('renders in Spanish', async () => {
    await loadCatalog('es');
    mockFetch.mockResolvedValue(CUSTOMISED);
    renderWithProvider(<CopilotAdoptionSettingsPage />, { language: 'es' });

    expect(await screen.findByText('Configuración de la puntuación de adopción de Copilot')).toBeVisible();
    expect(screen.getByText('Personalizada')).toBeVisible();
    expect(screen.getByText('Total: 100 %')).toBeVisible();
    expect(screen.getByText('Campeón a partir de: 75 → 90')).toBeVisible();
    expect(screen.getByRole('button', { name: 'Restablecer valores predeterminados' })).toBeEnabled();
    expect(screen.queryByText('Save')).toBeNull();
  });
});