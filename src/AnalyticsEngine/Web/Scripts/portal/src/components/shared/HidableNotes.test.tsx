import { describe, it, expect, beforeEach } from 'vitest';
import { screen, fireEvent } from '@testing-library/react';
import { renderWithProvider } from '../../test/renderWithProvider';
import HidableNotes from './HidableNotes';

describe('HidableNotes', () => {
  beforeEach(() => window.localStorage.clear());

  it('renders nothing when there is nothing to say', () => {
    renderWithProvider(<HidableNotes notes={[]} storageKey="test" />);
    expect(screen.queryByText('About these figures')).not.toBeInTheDocument();
    expect(screen.queryByRole('button')).not.toBeInTheDocument();
  });

  it('hides the notes behind a button that still says how many there are, and shows them again', () => {
    renderWithProvider(<HidableNotes notes={['First note.', 'Second note.']} storageKey="test" />);
    expect(screen.getByText('About these figures')).toBeInTheDocument();
    expect(screen.getByText('First note.')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Hide these notes' }));
    expect(screen.queryByText('First note.')).not.toBeInTheDocument();
    const show = screen.getByRole('button', { name: 'About these figures (2 notes)' });
    expect(show).toHaveAttribute('aria-expanded', 'false');

    fireEvent.click(show);
    expect(screen.getByText('Second note.')).toBeInTheDocument();
  });

  it('remembers the choice per report, and uses the singular for one note', () => {
    const { unmount } = renderWithProvider(<HidableNotes notes={['Only note.']} storageKey="reportA" />);
    fireEvent.click(screen.getByRole('button', { name: 'Hide these notes' }));
    unmount();

    renderWithProvider(
      <>
        <HidableNotes notes={['Only note.']} storageKey="reportA" />
        <HidableNotes notes={['Another report.']} storageKey="reportB" />
      </>,
    );
    expect(screen.getByRole('button', { name: 'About these figures (1 note)' })).toBeInTheDocument();
    expect(screen.queryByText('Only note.')).not.toBeInTheDocument();
    expect(screen.getByText('Another report.')).toBeInTheDocument();
  });

  it('leaves the hidden notes off a printout too', () => {
    renderWithProvider(<HidableNotes notes={['Note.']} storageKey="test" />);
    expect(screen.getByRole('button', { name: 'Hide these notes' })).toHaveAttribute('data-print', 'hide');
    fireEvent.click(screen.getByRole('button', { name: 'Hide these notes' }));
    expect(screen.getByRole('button', { name: 'About these figures (1 note)' })).toHaveAttribute('data-print', 'hide');
  });
});
