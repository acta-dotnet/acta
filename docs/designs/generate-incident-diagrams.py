# Single source for the incident-response architecture ring.
# Emits docs/designs/incident-response.svg (solid background, for GitHub/docs)
# and site/assets/ai-incident-response-ring.svg (transparent, for the site).
# Run from the repository root: python docs/designs/generate-incident-diagrams.py
cards = [
    # (cx, cy, namespace, jobs, peer_workers, accent)  accent: verdi = root, oxide = human gate
    (800, 200,  "incident-control",   ["respond-to-incident","escalate-incident","close-incident"], 3, "verdi"),
    (1168, 332, "evidence-collector", ["collect-logs","collect-metrics","collect-traces","collect-deployments"], 4, None),
    (1320, 650, "ai-diagnostics",     ["analyze-evidence","synthesize-diagnosis","propose-remediation"], 3, None),
    (1168, 968, "approval-gateway",   ["evaluate-action-policy","request-remediation-approval"], 2, "oxide"),
    (800, 1100, "remediation-runner", ["execute-remediation","verify-recovery","rollback-remediation"], 3, None),
    (432, 968,  "report-publisher",   ["build-incident-report","publish-incident-report"], 2, None),
    (280, 650,  "messenger",          ["notify-responders","send-incident-update"], 1, None),
    (432, 332,  "incident-ingest",    ["receive-alert","deduplicate-alert","open-incident"], 2, None),
]
W = 360

def card_svg(cx, cy, name, jobs, peers, accent):
    h = 66 + 29 * (len(jobs) - 1) + 22 + 26
    x, y = cx - W // 2, cy - h // 2
    stroke = {"verdi": "#2FD6A8", "oxide": "#D08A45"}.get(accent, "#2A3242")
    header_fill = {"verdi": "#2FD6A8", "oxide": "#D08A45"}.get(accent, "#2FD6A8")
    peers_label = "1 peer worker" if peers == 1 else f"{peers} peer workers"
    out = [f'    <g transform="translate({x},{y})">']
    out.append(f'      <rect width="{W}" height="{h}" rx="12" fill="#10151F" stroke="{stroke}" stroke-width="1.5"/>')
    out.append(f'      <text x="{W//2}" y="32" text-anchor="middle" font-size="15" letter-spacing="2" fill="{header_fill}">ns: {name}</text>')
    for i, j in enumerate(jobs):
        out.append(f'      <text x="{W//2}" y="{66 + 29*i}" text-anchor="middle" font-size="20" fill="#E8EDF5">{j}</text>')
    out.append(f'      <text x="{W//2}" y="{h - 14}" text-anchor="middle" font-size="13" fill="#66748A">{peers_label}</text>')
    out.append('    </g>')
    return "\n".join(out)

spokes = "\n".join(f'    <line x1="800" y1="650" x2="{cx}" y2="{cy}"/>' for cx, cy, *_ in cards)
cards_svg = "\n".join(card_svg(*c) for c in cards)

def doc(bg):
    bgrect = f'  <rect width="1600" height="1300" rx="16" fill="{bg}"/>\n' if bg else ""
    return f'''<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1600 1300" font-family="'IBM Plex Mono', Consolas, monospace">
  <title>AI incident-response demo on Acta: eight service-owned namespaces around one SQL work ledger</title>
{bgrect}  <g stroke="#3A4456" stroke-width="1.2" stroke-dasharray="3 5">
{spokes}
  </g>
  <circle cx="800" cy="650" r="220" fill="none" stroke="#3A4456" stroke-width="1"/>
  <circle cx="800" cy="650" r="200" fill="#10151F" stroke="#2FD6A8" stroke-width="2"/>
  <text x="800" y="625" text-anchor="middle" font-family="Fraunces, Georgia, serif" font-size="60" font-weight="600" fill="#E8EDF5">Acta<tspan fill="#2FD6A8">.</tspan></text>
  <text x="800" y="666" text-anchor="middle" font-size="17" letter-spacing="3" fill="#2FD6A8">ONE SQL LEDGER</text>
  <text x="800" y="700" text-anchor="middle" font-size="15" fill="#9BA7BA">jobs · leases · lineage</text>
  <text x="800" y="724" text-anchor="middle" font-size="15" fill="#9BA7BA">signals · checkpoints · events</text>
  <g>
{cards_svg}
  </g>
  <text x="800" y="1250" text-anchor="middle" font-size="14" fill="#66748A">each namespace is claimed by its own peer workers · the ledger is Acta state, not the services' business data</text>
</svg>
'''

open('docs/designs/incident-response.svg', 'w', encoding='utf-8').write(doc('#0B0E15'))
open('site/assets/ai-incident-response-ring.svg', 'w', encoding='utf-8').write(doc(None))
print("regenerated both diagrams")
