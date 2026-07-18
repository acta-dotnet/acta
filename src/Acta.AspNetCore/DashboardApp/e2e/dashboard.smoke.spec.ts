import { expect, test, type Page, type Route } from '@playwright/test';

const jobRef = 'job_00000000000000000000000042';
const timestamp = '2026-07-14T08:00:00Z';

const paged = <T>(items: T[]) => ({ items, nextCursor: null, hasMore: false, pageSize: 50, totalCount: items.length });

const job = {
  jobRef,
  lineageRootJobRef: null,
  parentJobRef: null,
  deduplicationKey: 'invoice-42',
  correlationKey: 'order-42',
  jobNamespace: 'billing',
  jobName: 'send-invoice',
  tenantId: null,
  status: 'failed',
  priority: 'normal',
  executionNumber: 1,
  failureCount: 1,
  inputFormatId: 1,
  nextRunAtUtc: null,
  leasedByWorkerId: null,
  leaseExpiresAtUtc: null,
  exclusiveKey: null,
  retentionUntilUtc: '2026-08-14T08:00:00Z',
  createdAtUtc: '2026-07-14T07:55:00Z',
  modifiedAtUtc: timestamp
};

const event = {
  jobEventId: 91,
  eventCode: 'job.failed',
  createdAtUtc: timestamp,
  jobNamespace: 'billing',
  jobRef,
  executionNumber: 1,
  fromStatus: 'executing',
  toStatus: 'failed',
  executionStatus: 'failed',
  durationMs: 125,
  reasonCode: 'handler-error',
  reasonMessage: 'Invoice provider timed out.'
};

async function mockDashboard(page: Page, options: { controls: boolean; onRestart?: () => void }): Promise<void> {
  await page.route('**/api/**', async (route: Route) => {
    const request = route.request();
    const url = new URL(request.url());
    const path = url.pathname.split('/api/')[1] ?? '';
    const json = (body: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });

    if (path === 'capabilities') {
      return json({ controlsEnabled: options.controls, version: 'test', provider: 'mock', confirmationHeader: 'X-Acta-Control' });
    }
    if (path === 'namespaces/admin') {
      return json(paged([{ id: 2, name: 'billing', status: 'active', ownerTeam: null, description: null, version: 1 }]));
    }
    if (path === 'overview') {
      return json({
        jobCount: 1,
        systemJobCount: 0,
        readyCount: 0,
        executingCount: 0,
        failedCount: 1,
        oldestReadyAgeSeconds: 0,
        unresolvedAlertCount: 0,
        unresolvedCriticalAlertCount: 0,
        deadWorkerCount: 0,
        staleWorkerCount: 0,
        dueSoonScheduleCount: 0
      });
    }
    if (path === 'jobs' && request.method() === 'GET') return json(paged([job]));
    if (path === 'alerts') return json(paged([]));
    if (path === 'workers') return json(paged([]));
    if (path === 'schedules') return json(paged([]));
    if (path === `jobs/${jobRef}`) return json(job);
    if (path === `jobs/${jobRef}/explain`) {
      return json({
        headline: 'The latest attempt failed in the invoice provider.',
        activeWait: null,
        lease: null,
        lastExecutedBy: 'worker-42',
        steps: [],
        reason: 'Invoice provider timed out.',
        nextActions: [{ kind: 'restart', description: 'Run the job again after checking the provider.' }]
      });
    }
    if (path === `jobs/${jobRef}/lineage`) {
      return json({ ancestors: [], job, steps: [], activeWait: null, children: [], childrenHasMore: false });
    }
    if (path === `jobs/${jobRef}/events`) return json(paged([event]));
    if (path === `jobs/${jobRef}/restart` && request.method() === 'POST') {
      if (!options.controls) return json({ title: 'Controls disabled.' }, 404);
      options.onRestart?.();
      return json({ jobRef, action: 'applied', status: 'ready', message: 'Job restarted.' });
    }

    return json({ title: `No mock for ${request.method()} ${path}` }, 404);
  });
}

test('job investigation: overview to failed job explanation and events', async ({ page }) => {
  await mockDashboard(page, { controls: true });
  await page.goto('/');

  await expect(page.getByRole('heading', { name: 'Recent failed jobs' })).toBeVisible();
  await page.getByRole('link', { name: jobRef }).click();
  await expect(page.getByRole('heading', { name: 'Evidence' })).toBeVisible();
  await expect(page.getByText('The latest attempt failed in the invoice provider.').first()).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Timeline' })).toBeVisible();
  await expect(page.getByText('Invoice provider timed out.').first()).toBeVisible();
});

test('safe single-job control sends exactly one restart request', async ({ page }) => {
  let restartRequests = 0;
  await mockDashboard(page, { controls: true, onRestart: () => restartRequests++ });
  await page.goto(`/#/jobs/${jobRef}?ns=billing`);

  await page.getByRole('button', { name: 'Run again' }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog.getByText(/External side effects.*may be repeated/)).toBeVisible();
  await dialog.getByLabel(/Reason \(required/).fill('Provider recovered; operator retry.');
  await dialog.getByRole('button', { name: 'Run again' }).click();

  await expect(page.getByText('Job restarted.')).toBeVisible();
  expect(restartRequests).toBe(1);
});

test('read-only deployment hides controls and rejects a direct request', async ({ page }) => {
  await mockDashboard(page, { controls: false });
  await page.goto(`/#/jobs/${jobRef}`);

  await expect(page.getByText('Read-only - controls disabled on this host.')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Run again' })).toHaveCount(0);
  const status = await page.evaluate(async (ref) => {
    const response = await fetch(`api/jobs/${ref}/restart`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-Acta-Control': 'true' },
      body: JSON.stringify({ reasonMessage: 'bypass attempt' })
    });
    return response.status;
  }, jobRef);
  expect(status).toBeGreaterThanOrEqual(400);
});

test('Jobs and Job Detail fit a mobile viewport without page overflow', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await mockDashboard(page, { controls: false });
  await page.goto('/#/jobs?ns=billing');

  await expect(page.getByRole('button', { name: 'Open navigation' })).toBeVisible();
  await expect(page.getByRole('link', { name: /send-invoice/ })).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);

  await page.getByRole('link', { name: /send-invoice/ }).click();
  await expect(page.getByRole('heading', { name: 'send-invoice' })).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
});

test('appearance defaults, themes, accents, text sizing, persistence, and reset stay coherent', async ({ page }) => {
  await mockDashboard(page, { controls: true });
  await page.addInitScript(() => {
    localStorage.removeItem('acta-appearance-v1');
    localStorage.removeItem('acta-theme');
    localStorage.removeItem('acta-palette');
    localStorage.removeItem('acta-density');
  });
  await page.goto('/');

  const golden = await page.evaluate(() => {
    const root = document.documentElement;
    const styles = getComputedStyle(root);
    const before = getComputedStyle(document.body, '::before');
    const tokens = [
      '--bg',
      '--panel',
      '--ink',
      '--muted',
      '--line',
      '--accent',
      '--accent-solid',
      '--nav-active-bg',
      '--grid',
      '--glow',
    ];
    return {
      theme: root.dataset.theme,
      accent: root.dataset.accent,
      textSize: root.dataset.textSize,
      colorScheme: root.style.colorScheme,
      tokens: Object.fromEntries(tokens.map((token) => [token, styles.getPropertyValue(token).trim()])),
      gridSize: before.backgroundSize,
      bodyBackground: getComputedStyle(document.body).backgroundImage,
    };
  });

  expect(golden).toEqual({
    theme: 'acta',
    accent: 'teal',
    textSize: 'default',
    colorScheme: 'dark',
    tokens: {
      '--bg': '#0b0f17',
      '--panel': '#11161f',
      '--ink': '#eef2f7',
      '--muted': '#9aa6b6',
      '--line': '#232c40',
      '--accent': '#64d8c7',
      '--accent-solid': '#077a70',
      '--nav-active-bg': '#16302b',
      '--grid': 'rgba(100, 216, 199, 0.05)',
      '--glow': 'rgba(100, 216, 199, 0.1)',
    },
    gridSize: '46px 46px, 46px 46px',
    bodyBackground: expect.stringContaining('radial-gradient'),
  });

  const trigger = page.getByRole('button', { name: /Appearance Acta/ });
  await trigger.click();
  const actaRadio = page.getByRole('radio', { name: /^Acta/ });
  await expect(actaRadio).toBeFocused();

  const popoverGeometry = await page.evaluate(() => {
    const nav = document.querySelector<HTMLElement>('nav.side');
    const sideScroll = document.querySelector<HTMLElement>('.side-scroll');
    const popover = document.querySelector<HTMLElement>('.appearance-menu .popover');
    if (!nav || !sideScroll || !popover) return null;

    const navRect = nav.getBoundingClientRect();
    const popoverRect = popover.getBoundingClientRect();
    const visiblePoint = document.elementFromPoint(popoverRect.right - 2, popoverRect.top + 20);
    return {
      extendsPastSidebar: popoverRect.right > navRect.right,
      rightEdgeVisible: visiblePoint !== null && popover.contains(visiblePoint),
      navigationHasHorizontalOverflow: sideScroll.scrollWidth > sideScroll.clientWidth,
    };
  });
  expect(popoverGeometry).toEqual({
    extendsPastSidebar: true,
    rightEdgeVisible: true,
    navigationHasHorizontalOverflow: false,
  });

  await page.getByText('Light', { exact: true }).click();
  await page.getByText('Violet', { exact: true }).click();
  await page.getByText('Large', { exact: true }).click();

  const lightViolet = await page.evaluate(() => {
    const root = document.documentElement;
    const styles = getComputedStyle(root);
    return {
      theme: root.dataset.theme,
      accent: root.dataset.accent,
      textSize: root.dataset.textSize,
      colorScheme: root.style.colorScheme,
      background: styles.getPropertyValue('--bg').trim(),
      panel: styles.getPropertyValue('--panel').trim(),
      accentToken: styles.getPropertyValue('--accent').trim(),
      rowHeight: styles.getPropertyValue('--grid-row-height').trim(),
      stored: JSON.parse(localStorage.getItem('acta-appearance-v1') ?? 'null'),
    };
  });
  expect(lightViolet).toEqual({
    theme: 'light',
    accent: 'violet',
    textSize: 'large',
    colorScheme: 'light',
    background: '#f3f6fa',
    panel: '#ffffff',
    accentToken: '#6550b9',
    rowHeight: '44px',
    stored: { version: 1, theme: 'light', accent: 'violet', textSize: 'large' },
  });

  await page.getByText('Amber', { exact: true }).click();
  const neutralsAfterAccentSwitch = await page.evaluate(() => {
    const styles = getComputedStyle(document.documentElement);
    return [styles.getPropertyValue('--bg').trim(), styles.getPropertyValue('--panel').trim()];
  });
  expect(neutralsAfterAccentSwitch).toEqual(['#f3f6fa', '#ffffff']);

  await page.getByText('Paper', { exact: true }).click();
  await page.getByText('Crimson', { exact: true }).click();
  await page.getByText('Small', { exact: true }).click();
  const paper = await page.evaluate(() => {
    const rootStyles = getComputedStyle(document.documentElement);
    const heading = document.querySelector<HTMLElement>('.page-head h1');
    return {
      colorScheme: document.documentElement.style.colorScheme,
      background: rootStyles.getPropertyValue('--bg').trim(),
      panel: rootStyles.getPropertyValue('--panel').trim(),
      radius: rootStyles.getPropertyValue('--radius-panel').trim(),
      rowHeight: rootStyles.getPropertyValue('--grid-row-height').trim(),
      headingFont: heading ? getComputedStyle(heading).fontFamily : '',
      ruledBackground: getComputedStyle(document.body, '::before').backgroundImage,
    };
  });
  expect(paper).toEqual({
    colorScheme: 'light',
    background: '#eee5d3',
    panel: '#fffaf0',
    radius: '3px',
    rowHeight: '34px',
    headingFont: expect.stringContaining('Segoe UI'),
    ruledBackground: expect.stringContaining('repeating-linear-gradient'),
  });

  await page.getByRole('button', { name: 'Restore defaults' }).click();
  await expect(page.getByRole('radio', { name: /^Acta/ })).toBeChecked();
  await expect(page.getByRole('radio', { name: 'Teal', exact: true })).toBeChecked();
  await expect(page.getByRole('radio', { name: 'Default', exact: true })).toBeChecked();

  await page.keyboard.press('Escape');
  await expect(page.getByRole('button', { name: /Appearance Acta/ })).toBeFocused();
});

test('appearance dialog stays usable at 320px by 500px with Large text', async ({ page }) => {
  await page.setViewportSize({ width: 320, height: 500 });
  await mockDashboard(page, { controls: true });
  await page.goto('/');

  await page.getByRole('button', { name: 'Open navigation' }).click();
  await page.getByRole('button', { name: /Appearance/ }).click();

  const dialog = page.getByRole('dialog', { name: 'Appearance' });
  await expect(dialog).toBeVisible();
  await dialog.getByText('Large', { exact: true }).click();
  const box = await dialog.boundingBox();
  expect(box).not.toBeNull();
  expect(box!.x).toBeGreaterThanOrEqual(0);
  expect(box!.y).toBeGreaterThanOrEqual(0);
  expect(box!.x + box!.width).toBeLessThanOrEqual(320);
  expect(box!.y + box!.height).toBeLessThanOrEqual(500);

  await dialog.getByRole('button', { name: 'Restore defaults' }).scrollIntoViewIfNeeded();
  await dialog.getByRole('button', { name: 'Restore defaults' }).click();
  await expect(dialog.getByRole('radio', { name: 'Default' })).toBeChecked();
});
