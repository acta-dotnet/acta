import { render, screen } from '@testing-library/svelte';
import { describe, expect, it } from 'vitest';
import Pager from './Pager.svelte';

describe('Pager', () => {
  it('offers only the page sizes the API serves', () => {
    render(Pager, { pageSize: 50 });
    const options = Array.from((screen.getByLabelText('Page size') as HTMLSelectElement).options).map((option) => option.value);
    expect(options).toEqual(['25', '50', '100']);
  });
});
