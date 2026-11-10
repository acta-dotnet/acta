# Acta Dashboard UI and UX specification

This is the maintained UI and UX specification for Acta.Dashboard and its DashboardApp frontend. Use it when building new screens, changing existing interfaces, and reviewing dashboard changes.

Keep this specification in source control alongside the project. Update it whenever a design decision is adopted or revised, together with the implementation and its verification.

Implement shared patterns through `DashboardApp/src/components` and the design tokens in `DashboardApp/src/styles.css` and `DashboardApp/src/theme`. Keep shared conventions consistent across overview, lists, forms, and detail screens.

## Direction

Acta should feel calm, technically precise, modern, and readable. Focus is the dashboard's interface model, with clear hierarchy, contextual actions, full-width detail pages, and information revealed when needed.

- Use flat surfaces, fine borders, and restrained depth.
- Give status, the current problem, and the relevant next action the strongest hierarchy.
- Make secondary information available through clearly named disclosures.
- Preserve the operational meaning of every status, permission, control, and read.
- Use an 8px spacing rhythm, 10–12px panel radii, and consistent line icons.
- Use one lightweight outline icon family throughout the dashboard. Keep the copy glyph slightly finer and smaller.
- Keep animation minimal and respect reduced-motion preferences.

## Colors and themes

- The main website supports light and dark themes. Paper was removed from the website.
- The dashboard supports coordinated light, dark, and paper themes with the same component structure.
- Light uses `#F7F9FC` backgrounds, white surfaces, and dark navy text around `#172033`.
- Dark uses `#0E1625` backgrounds, elevated navy surfaces around `#19263A`, and high-contrast pale text.
- Paper uses warm ivory around `#FAF7F1`, warm-white surfaces, and deep charcoal text.
- Light and paper body text, code, labels, and technical values must remain dark enough to read comfortably.
- Website light code samples follow the readability of a Visual Studio light palette.
- Use mint, lavender, and related low-saturation colors sparingly. Ambient gradients belong near page edges and selected navigation.
- Keep the accepted broad, subtle background arc at the top-right edge.
- Use semantic status colors alongside text and icons.
- Anvil uses the same visual refinement with a white-only theme.

## Typography and brand

- Use a modern sans-serif stack led by Inter or Geist, with system fallbacks.
- Reserve monospaced typography for identifiers, expressions, code, and payloads.
- Use tabular numbers where values are compared.
- Keep the Acta mark aligned optically with the wordmark's capital height.
- Align the website header and footer brand to the same horizontal position.
- Theme controls must preserve scroll position.

## Application shell

- Keep the grouped Operate, Configure, and Admin navigation.
- Use a compact, consistent sidebar with a clear selected state and a subtle mint-to-lavender fill.
- Place the appearance control quietly at the bottom of the sidebar.
- Preserve namespace selection, search, sidebar collapse, mobile navigation, and keyboard access.
- Share typography, surfaces, borders, action styles, and spacing across overview, lists, forms, and details.
- Keep content readable at desktop widths and usable on narrow screens and larger text settings.

## Focus detail pages

- Use one full-width content column.
- Show the entity name once as the page heading.
- Put status, scope, and a compact reference directly under the heading.
- Present the current state or problem before secondary facts.
- Keep a relevant primary action beside the state when one applies.
- Put secondary and destructive actions in a compact Actions menu in the page header. Preserve confirmations and audit-reason requirements.
- Hide action controls that do not apply or that the operator cannot use.
- Use named disclosures for activity, stored data, policy editing, metadata, and technical details.
- Render schedules, checkpoints, lineage, and bindings only when relevant data exists.
- Keep related links as a compact footer row.
- Keep explanatory copy close to the control or fact it explains.
- Avoid large circular status emblems and repeated status panels.

## References and dates

- Use one compact display format consisting of the type without its underscore, an ellipsis, and the final eight hexadecimal digits. Examples include `job…72c9e1a4`, `wrk…72c9e1a4`, and `alr…72c9e1a4`.
- Keep full references available through hover text, accessible labels, full-value inspection, and copy controls.
- Copy the complete stored reference, including its original underscore. Use the full reference in navigation, API calls, and search.
- Treat the condensed text as a display label. Eight trailing hexadecimal digits can collide. Keep the complete identifier available to inspect.
- Show copy controls on hover and keyboard focus, and keep them visible on touch screens.
- Use a small, light-outline copy glyph. Keep its visual weight below the reference text.
- Schedules currently use readable namespace, job, and schedule names as their public keys.
- Put Created, Modified, row versions, correlation keys, and other audit metadata in technical details.
- Show operationally relevant times such as deadlines, heartbeats, and next runs near their context.
- Show relative times by default. Make the exact timestamp available on hover and through keyboard-accessible inspection.

## Job detail

- Use the Focus single-page structure.
- Show failure reasons, active waits, deadlines, blocked dependencies, and useful recovery actions near the top.
- Show a successful result prominently when one exists.
- Collapse Activity, stored input and checkpoints, attached schedules, and technical details independently.
- Keep execution history and timeline filters available within Activity.
- Showing execution history puts the attempt summaries in focus. Walking an execution returns to its filtered event timeline.
- Preserve existing section and execution deep links by opening their matching disclosures.
- Keep input amendment, signals, clone, tags, incident copying, and all existing job controls available under their permissions.
- Preserve permanent-job firing and attempt-budget semantics.

## Job lineage

- Show the current job as “This job” and direct children as connected, selectable nodes.
- Make failed children easy to spot and inspect.
- Show a compact status rollup and filters when a family is large.
- Show ancestors as a navigable path.
- Inspect a child's real state within the page and load its details on demand.
- Allow drilling into descendants and returning through the lineage path.
- Use a readable vertical hierarchy on narrow screens.
- State when an API read is capped. Derive descendant information from actual reads.

## Other detail screens

- Schedules emphasize their expression, time zone, current state, and next occurrence. Advanced delivery policy and editing use disclosures.
- Calendars emphasize the shown definition and forecast. Keep version navigation compact and show bound schedules when present.
- Definitions emphasize effective policy. Place the full override editor, contract, and supporting metadata behind disclosures.
- Workers emphasize health, heartbeat, and execution capacity. Keep process identity, exact audit times, and runtime versions in technical details.
- Alerts emphasize the incident and acknowledgment or resolution. Show delivery problems clearly and keep routine delivery metadata secondary.
- Namespaces and tenants emphasize current availability and human context. Use disclosures for editing and supporting metadata.

## Lists, tables, and overview

- Preserve filtering, sorting, paging, selection, virtualization, bulk controls, export, and navigation behavior.
- Use low-contrast row separators and clear column headings.
- Keep technical references consistent and long text safely inspectable.
- Use compact empty-state rows with an icon, heading, and short message.
- Use small section icons for Workers and Outbox relays.
- Give operational problems prominence while keeping healthy and empty states compact.

## Verification

- Check every registered screen and each changed detail disclosure.
- Exercise light, dark, and paper dashboard themes, narrow screens, larger text, and keyboard navigation.
- Verify authorized mutations through their existing endpoints and confirmation flows.
- Keep browser and component tests aligned with the chosen navigation and disclosure structure.
- Verify that loading, missing, empty, error, permission-limited, and partial-data states remain understandable.
