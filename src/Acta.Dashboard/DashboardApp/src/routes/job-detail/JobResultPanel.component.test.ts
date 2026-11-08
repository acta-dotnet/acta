import { render } from '@testing-library/svelte';
import { describe, expect, it } from 'vitest';
import JobResultPanel from './JobResultPanel.svelte';

describe('JobResultPanel', () => {
  it('says a job whose definition declares no result type returns no result', () => {
    const { getByText, queryByText } = render(JobResultPanel, { result: null, resultTypeName: null });
    expect(getByText('This job returns no result.')).toBeTruthy();
    expect(queryByText('No result yet')).toBeNull();
  });

  it('waits for the result of a job that declares one', () => {
    const { getByText } = render(JobResultPanel, { result: null, resultTypeName: 'Billing.InvoiceReceipt' });
    expect(getByText('No result yet')).toBeTruthy();
  });
});
