import { render, screen } from '@testing-library/angular';
import { describe, expect, it } from 'vitest';
import { EmptyStateComponent } from './empty-state';

describe('EmptyStateComponent', () => {
  it('renders the heading', async () => {
    await render(EmptyStateComponent, { inputs: { heading: 'No items yet' } });

    expect(screen.getByRole('heading', { name: 'No items yet' })).toBeTruthy();
  });

  it('renders the optional message when provided', async () => {
    await render(EmptyStateComponent, {
      inputs: { heading: 'No items yet', message: 'Create one to get started.' },
    });

    expect(screen.getByText('Create one to get started.')).toBeTruthy();
  });

  it('omits the message paragraph when not provided', async () => {
    const { container } = await render(EmptyStateComponent, { inputs: { heading: 'No items yet' } });

    expect(container.querySelector('.empty-state p')).toBeNull();
  });
});
