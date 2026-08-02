import { render, screen } from '@testing-library/angular';
import { describe, expect, it } from 'vitest';
import { StatusPillComponent } from './status-pill';

describe('StatusPillComponent', () => {
  it('renders the status text', async () => {
    await render(StatusPillComponent, { inputs: { status: 'Active' } });

    expect(screen.getByText('Active')).toBeTruthy();
  });

  it.each([
    ['Draft', 's-draft'],
    ['Active', 's-approved'],
    ['Archived', 's-neutral'],
    ['Something else', 's-neutral'],
  ])('maps %s to the %s tone class', async (status, toneClass) => {
    await render(StatusPillComponent, { inputs: { status } });

    expect(screen.getByText(status).className).toContain(toneClass);
  });
});
