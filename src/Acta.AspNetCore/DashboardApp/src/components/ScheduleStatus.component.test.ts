import { render, screen } from '@testing-library/svelte';
import { afterEach, describe, expect, it, vi } from 'vitest';
import ScheduleStatus from './ScheduleStatus.svelte';

afterEach(() => {
  vi.useRealTimers();
});

describe('ScheduleStatus', () => {
  it('advances the relative label after a timed pause expires', async () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-07-14T12:00:00Z'));
    render(ScheduleStatus, { status: 'paused', pausedUntilUtc: '2026-07-14T12:00:01Z' });

    expect(screen.getByText('resumes in 1s')).toBeTruthy();

    await vi.advanceTimersByTimeAsync(2_000);

    expect(screen.getByText('resumes 1s ago')).toBeTruthy();
  });
});
