import { render, screen, fireEvent } from '@testing-library/angular';
import { describe, expect, it, vi } from 'vitest';
import { ConfirmDialogComponent } from './confirm-dialog';

describe('ConfirmDialogComponent', () => {
  it('renders the title, message and confirm label', async () => {
    await render(ConfirmDialogComponent, {
      inputs: { title: 'Delete item?', message: 'This cannot be undone.', confirmLabel: 'Delete' },
    });

    expect(screen.getByText('Delete item?')).toBeTruthy();
    expect(screen.getByText('This cannot be undone.')).toBeTruthy();
    expect(screen.getByText('Delete')).toBeTruthy();
  });

  it('defaults the confirm label to "Confirm"', async () => {
    await render(ConfirmDialogComponent, { inputs: { title: 't', message: 'm' } });

    expect(screen.getByText('Confirm')).toBeTruthy();
  });

  it('emits confirm exactly once when the confirm button is pressed', async () => {
    const confirm = vi.fn();
    await render(ConfirmDialogComponent, {
      inputs: { title: 't', message: 'm', confirmLabel: 'Yes' },
      on: { confirm },
    });

    fireEvent.click(screen.getByText('Yes'));

    expect(confirm).toHaveBeenCalledOnce();
  });

  it('emits close when cancel is pressed', async () => {
    const close = vi.fn();
    await render(ConfirmDialogComponent, { inputs: { title: 't', message: 'm' }, on: { close } });

    fireEvent.click(screen.getByText('Cancel'));

    expect(close).toHaveBeenCalledOnce();
  });

  it('shows a busy label while working', async () => {
    await render(ConfirmDialogComponent, {
      inputs: { title: 't', message: 'm', confirmLabel: 'Yes', busy: true },
    });

    expect(screen.getByText('Working…')).toBeTruthy();
  });

  it('surfaces an error inline with an alert role', async () => {
    await render(ConfirmDialogComponent, {
      inputs: { title: 't', message: 'm', error: 'Server said no.' },
    });

    expect(screen.getByRole('alert').textContent).toContain('Server said no.');
  });
});
