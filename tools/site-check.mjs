// Release check for useacta.net (site/): version literals, nav and footer parity across pages, and
// horizontal overflow at phone and tablet widths. Run from the repo root once the released packages
// resolve from nuget.org: `node tools/site-check.mjs --version 1.0.0-rc.3`. Uses the dashboard's
// Playwright install, so `npm ci` in src/Acta.AspNetCore/DashboardApp must have run once.
import { readdirSync, readFileSync } from "node:fs";
import { pathToFileURL } from "node:url";
import { resolve } from "node:path";

const repoRoot = resolve(import.meta.dirname, "..");
const siteDir = resolve(repoRoot, "site");
const versionArg = process.argv.indexOf("--version");
const version = versionArg >= 0 ? process.argv[versionArg + 1] : null;
if (!version) {
  console.error("usage: node tools/site-check.mjs --version <released version>");
  process.exit(2);
}

const pages = readdirSync(siteDir).filter((f) => f.endsWith(".html")).sort();
const failures = [];

// 1. Every version literal under site/ names the released version.
const literal = /\b1\.0\.0(?:-rc\.\d+)?\b|\b[1-9]\d*\.\d+\.\d+(?:-rc\.\d+)?\b/g;
for (const file of readdirSync(siteDir).filter((f) => /\.(html|txt|xml)$/.test(f))) {
  const text = readFileSync(resolve(siteDir, file), "utf8");
  for (const m of text.matchAll(literal)) {
    if (m[0] !== version && /^1\.0\.0/.test(m[0])) {
      failures.push(`${file}: version literal '${m[0]}' is not the released '${version}'`);
    }
  }
}

// 2. Nav and footer are identical across pages, apart from the Home/Get started first link.
const block = (text, open, close) => {
  const s = text.indexOf(open);
  const e = text.indexOf(close, s);
  // Subpages anchor Compare as /#compare while the homepage uses #compare; the same link either way.
  return s < 0 || e < 0
    ? null
    : text
        .slice(s, e)
        .replace(/\r/g, "")
        .replace(/<a href="\/(start)?">[^<]*<\/a>/, "")
        .replace('href="/#', 'href="#')
        .trim();
};
const navs = new Map();
const footers = new Map();
for (const file of pages) {
  const text = readFileSync(resolve(siteDir, file), "utf8");
  // A redirect stub (concepts.html) carries no nav or footer to align; no reader ever sees it.
  if (/<meta http-equiv="refresh"/.test(text)) continue;
  navs.set(file, block(text, '<nav aria-label="Primary links">', "</nav>"));
  footers.set(file, block(text, "<footer>", "</footer>"));
}
for (const [name, map] of [["nav", navs], ["footer", footers]]) {
  const reference = map.get("index.html");
  for (const [file, value] of map) {
    if (value !== reference) failures.push(`${file}: ${name} differs from index.html`);
  }
}

// 3. No horizontal overflow at 360, 390, and 768 px.
const playwright = pathToFileURL(resolve(repoRoot, "src/Acta.AspNetCore/DashboardApp/node_modules/playwright/index.mjs")).href;
const { chromium } = await import(playwright);
const browser = await chromium.launch();
for (const width of [360, 390, 768]) {
  const ctx = await browser.newContext({ viewport: { width, height: 844 }, isMobile: width < 700 });
  for (const file of pages) {
    const page = await ctx.newPage();
    await page.goto(pathToFileURL(resolve(siteDir, file)).href, { waitUntil: "load" });
    await page.waitForTimeout(300);
    const { sw, vw } = await page.evaluate(() => ({
      sw: document.documentElement.scrollWidth,
      vw: document.documentElement.clientWidth,
    }));
    if (sw > vw) failures.push(`${file} at ${width}px: scrolls horizontally (${sw} > ${vw})`);
    await page.close();
  }
  await ctx.close();
}
await browser.close();

if (failures.length > 0) {
  console.error(`site-check: ${failures.length} failure(s)`);
  for (const f of failures) console.error(`  ${f}`);
  process.exit(1);
}
console.log(`site-check: ${pages.length} pages, version ${version}, nav and footer aligned, no overflow at 360/390/768`);
