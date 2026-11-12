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
- Paper is a reading mode, easy on the eyes at night, that reads like a book's pages laid on a desk: a dimmed sepia desk around `#EEE5D2`, sections on sheets of a lighter paper around `#F3EBDB` with hairline edges and a small radius, warm brown ink around `#3A2E22` a little short of black, and a reading serif for all but code and identifiers. Nothing on it is pure white: fields, menus, and dialogs take the lightest paper, `#F8F2E6`.
- Paper's status is coloured words with their mark, never a pill, and its links are ink with a quiet accent underline. It draws no ambient gradients or glow, and keeps a soft shadow only on menus and dialogs.
- Light and paper body text, code, labels, and technical values must remain dark enough to read comfortably.
- Dark text on a light ground reads thinner than light text on a dark one, so the light theme sets running text at weight 450 where the face has it.
- Website light code samples follow the readability of a Visual Studio light palette.
- Use mint, lavender, and related low-saturation colors sparingly. Ambient gradients belong near page edges and selected navigation.
- Keep the subtle background arc small, a curve tucked into the top-right corner, never a band across the page. The background carries no other art: shapes, ribbons, sweeps, colour pools, and translucent panels were tried and read as distracting behind operational data.
- Use semantic status colors alongside text and icons.
- A filled control in a mid-tone accent carries white text, as paper's do, on the accent's solid step or the nearest deeper one that gives white 4.5:1. A light-tint accent, the default mint or amber, keeps its bright fill with dark text.
- Anvil uses the same visual refinement with a white-only theme.

## Typography and brand

- Use a modern sans-serif stack led by Inter or Geist, with system fallbacks. On Windows 11 the fallback is the variable reading face, `Segoe UI Variable Text`.
- Paper sets text in a reading serif (Iowan Old Style or Charter on macOS, Sitka Text or Cambria on Windows) with lining figures, since a serif's old-style zero reads as the letter o.
- Reserve monospaced typography for identifiers, expressions, code, and payloads.
- Use tabular numbers where values are compared.
- Keep the Acta mark aligned optically with the wordmark's capital height.
- Align the website header and footer brand to the same horizontal position.
- Every website page carries the same header and footer, and one 1328px frame holds the header, the content, and the footer, so nothing moves between pages. The benchmark page is generated with that frame.
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
- Hide action controls that do not apply or that the operator cannot use. Once an operator could act on a page, keep its controls mounted, so a request refused by a revoked permission still shows its message.
- Show the outcome of an action started from the Actions menu beside the state, never only inside the menu, which may be closed.
- A control rendered in two places, beside the state and in the menu, shares one in-flight guard: a pending request disables both.
- The menu leaves out only the action the state panel actually shows. When the panel holds something else, such as a failed child or a signal sender, the menu keeps every control.
- Keep a confirmation's own clicks and Escape inside it, so the menu that opened it stays open and focus returns to the opener.
- Use named disclosures for activity, stored data, tags, policy editing, metadata, and technical details.
- Tags are a disclosure of their own on every entity page, just before the technical details, with the tag count as its hint. Operators label and find work by them, so they never hide among identifiers. Leave the disclosure out when the entity has no tags and the operator cannot add one.
- Render schedules, checkpoints, lineage, and bindings only when relevant data exists.
- Keep related links as a compact footer row.
- Keep explanatory copy close to the control or fact it explains.
- Avoid large circular status emblems and repeated status panels.

## References and dates

- Use one compact display format consisting of the type without its underscore, an ellipsis, and the final eight hexadecimal digits. Examples include `job…72c9e1a4`, `wrk…72c9e1a4`, and `alr…72c9e1a4`.
- Keep full references available on hover, through accessible labels, full-value inspection, and copy controls.
- Copy the complete stored reference, including its original underscore. Use the full reference in navigation, API calls, and search.
- Treat the condensed text as a display label. Eight trailing hexadecimal digits can collide. Keep the complete identifier available to inspect.
- Show copy controls on hover and keyboard focus, and keep them visible on touch screens.
- Use a small, light-outline copy glyph. Keep its visual weight below the reference text, and centre it on the reference's digits, beside the compact reference and in the full-reference popover alike.
- Schedules currently use readable namespace, job, and schedule names as their public keys.
- Put Created, Modified, row versions, correlation keys, and other audit metadata in technical details.
- Show operationally relevant times such as deadlines, heartbeats, and next runs near their context.
- Show relative times by default. Make the exact timestamp available on hover and through keyboard-accessible inspection.
- A compact reference or time opens its full value in a small card just below itself, never in the middle of the screen. Resting the mouse shows the card, and leaving both hides it; a click, a tap, or Enter pins it until Escape or a click elsewhere.
- Inspectable references and times grow their hit area to the 24px target floor without changing the line they sit on, and add no space before the punctuation that follows them. A copy control does the same around its glyph.

## Job detail

- Use the Focus single-page structure.
- Show failure reasons, active waits, deadlines, blocked dependencies, and useful recovery actions near the top.
- Show a successful result prominently when one exists.
- Collapse Activity, stored input and checkpoints, attached schedules, and technical details independently.
- Keep execution history and timeline filters available within Activity.
- Draw the activity timeline as the website draws its execution record: a soft round mark for each event, a hairline between two marks that touches neither, the event's title in the running weight with its reason beneath, and the time with the event's code at the right. Name each attempt in a plain line above its events. The mark alone carries the tone, and only the newest settled outcome takes a solid mark.
- Showing execution history puts the attempt summaries in focus. Walking an execution returns to its filtered event timeline.
- Preserve existing section and execution deep links by opening their matching disclosures.
- Keep input amendment, signals, clone, tags, incident copying, and all existing job controls available under their permissions.
- Preserve permanent-job firing and attempt-budget semantics. A retrying job states its budget beside its failures: "fails permanently at N", or "each firing gets N attempts" on a permanent job.
- When a job turns terminal, the timeline reads its newest events once more, since the last poll can precede them.
- A not-found page names the reference that was requested.

## Job lineage

- Show the current job as “This job” and direct children as connected, selectable nodes.
- Make failed children easy to spot and inspect.
- Show a compact status rollup and filters when a family is large.
- Show ancestors as a navigable path. An explored branch's path starts below this job: the API lists ancestors farthest first.
- Give every child node its compact reference, since siblings often share a job name.
- Inspect a child's real state within the page and load its details on demand. A changed parent read refreshes the inspected child and the explored branch.
- Allow drilling into descendants and returning through the lineage path. Inspecting this job's failed child leaves an explored branch.
- Use a readable vertical hierarchy on narrow screens.
- State when an API read is capped. Derive descendant information from actual reads.

## Other detail screens

- Schedules emphasize their expression, time zone, current state, and next occurrence. Advanced delivery policy and editing use disclosures. An active schedule whose evaluation is past due by more than the Overview's schedule-lag envelope leads with "Overdue for evaluation".
- Calendars emphasize the shown definition and forecast. Keep version navigation compact and show bound schedules when present.
- Definitions emphasize effective policy. Place the full override editor, contract, and supporting metadata behind disclosures.
- Workers emphasize health, heartbeat, and execution capacity. Keep process identity, exact audit times, and runtime versions in technical details.
- Alerts emphasize the incident and acknowledgment or resolution. Show delivery problems clearly and keep routine delivery metadata secondary.
- Namespaces and tenants emphasize current availability and human context. Use disclosures for editing and supporting metadata.

## Lists, tables, and overview

- Preserve filtering, sorting, paging, selection, virtualization, bulk controls, export, and navigation behavior.
- Use low-contrast row separators and clear column headings.
- Keep technical references consistent and long text safely inspectable.
- Tell a system job by its reserved `sys.` prefix, set in the muted tone before the rest of the name, as a reference's type is. Put no mark after a name in its cell: text of varying length gives it a different place on each row, and the column's ellipsis hides it first.
- A table never scrolls sideways for one long value. The value ends in an ellipsis on its row and opens in full in its card.
- A list's row actions stay in view at a 1440px window. Its prose cell gives way first: an alert's title and message share one cell that ends in an ellipsis, and the alert page shows both whole.
- Use compact empty-state rows with an icon, heading, and short message.
- Use small section icons for Workers and Outbox relays.
- Give operational problems prominence while keeping healthy and empty states compact.
- Freshness is one framed indicator of equal cells: a small vertical meter, then reload, then pause. It is never a ticking counter. On a view that polls, the meter drains to the bottom as the next refresh approaches and refills when data arrives. The meter takes the status colour, and words join it only for a state worth reading: paused, failed, offline, or session ended. A view that does not refresh itself shows a still meter and its data's age to the minute.
- Links inside a warning or failure banner use the text colour with an underline. The accent reads as healthy there.

## Accessibility

- The target is WCAG 2.2 AA in every theme.
- A control's accessible name contains the words shown on it. A label never replaces a control's own text, and a dropdown is named by its label and its value, as in "Namespace: billing". A compact reference is the one exception: it is named with the whole reference.
- A labelled element that is not a control has a role: a drawn bar is an image, a set of cards a group.
- Every control shows keyboard focus as the same two-pixel accent ring, a form field included. The quick-search field, alone in its palette, shows its caret.
- Placeholder text takes the muted text colour: it often carries a field's only hint.
- A link that shares a line with plain text, as in an action's message, is underlined in every theme. Colour alone never marks a link.
- On a narrow screen a list row becomes a card, and every cell takes the card's whole width with its label beside it. Two cells never share a line: a wide one squeezes its neighbour to nothing.

## Verification

- Check every registered screen and each changed detail disclosure.
- Exercise light, dark, and paper dashboard themes, narrow screens, larger text, and keyboard navigation.
- Verify authorized mutations through their existing endpoints and confirmation flows.
- Keep browser and component tests aligned with the chosen navigation and disclosure structure.
- The smoke suite scans the states its fixtures stage with the WCAG 2.2 A and AA rules, in each theme, and a violation fails it. Each state names the screen it is on, and a screen with no state fails the suite; a new overlay joins the table. An exception names one rule and one element, with its reason.
- The scan finds what a machine can decide and does not establish conformance. Before a release, by hand: walk every menu, dialog, and popover by keyboard; see the focus ring on each control, and focus return to what opened an overlay; read the job investigation path with a screen reader; check each list and detail screen at 200% and 400% zoom and at phone width.
- Exercise controls, lineage, and catalog workflows against a real backend as well as the fixture-backed browser tests.
- Website screenshots of the dashboard come from the final build, with fixture text taken from what the server actually returns.
- Verify that loading, missing, empty, error, permission-limited, and partial-data states remain understandable.
