# SOC 2 Audit Prep Checklist — FHIRBridge

A step-by-step run-of-show from "we want a SOC 2 report" to a clean **Type II** opinion. Work top to bottom; each phase gates the next. Checkbox items are the operational tasks; `[TO ASSIGN]`/`[DEFINE]` are decisions to make at kickoff.

**Owner of this checklist:** [TO ASSIGN — likely the Security Official] · **Kickoff date:** [DEFINE]

---

## Phase 0 — Kickoff & ownership

- [ ] Name an accountable **SOC 2 program owner** [TO ASSIGN].
- [ ] Assign an **owner per evidence area** (fill `evidence-matrix.md` Owner column).
- [ ] Secure **executive sponsorship** and budget (auditor fees + internal time).
- [ ] Decide whether to use a **compliance-automation platform** (Vanta / Drata / Secureframe / OneTrust) to collect evidence continuously — strongly recommended for Type II.

## Phase 1 — Scope the system

- [ ] Define the **system boundary**: which services, environments (prod in scope; staging/dev noted), data stores (SQL Server, Key Vault), and infrastructure (Azure, docker-compose) are covered.
- [ ] Identify **subservice organizations** (Azure) and decide **carve-out vs. inclusive** method (usually carve-out; confirm with auditor).
- [ ] Write the **system description** (the report's Section III narrative): what FHIRBridge does, data flows, ePHI handling, and the control environment.
- [ ] Document **service commitments & system requirements** you make to customers (informs the criteria).

## Phase 2 — Pick the Trust Services Criteria

- [ ] Confirm scope: **Security (CC1–CC9) required** + **Availability (A1)** + **Confidentiality (C1)** recommended. [DEFINE — confirm PI/Privacy are excluded this cycle.]
- [ ] Map each in-scope criterion to controls using `../hipaa-soc2-control-matrix.md` and `evidence-matrix.md`.
- [ ] Record any criteria **not applicable** with rationale.

## Phase 3 — Remediate readiness gaps

Work the **critical-path gaps** from `readiness-assessment.md` first. These are organizational — the technical controls are largely ready.

- [ ] **Formal risk assessment** (CC3): run it, produce a **risk register** (methodology, likelihood × impact, treatment, owners); schedule annual refresh.
- [ ] **Ratify policies** (CC1): complete Approval blocks (signatures + dates) in `../policies/`; publish org chart, job descriptions, code of conduct + acknowledgements.
- [ ] **Management oversight** (CC1/CC4): schedule a recurring (quarterly) security review; keep **minutes**.
- [ ] **User access reviews** (CC6): stand up a quarterly review with reviewer sign-off; retain records.
- [ ] **JML process** (CC6): document joiner/mover/leaver tied to HR events; capture evidence (tickets, timestamps).
- [ ] **Change management** (CC8): document the process; enable **branch protection + required reviews**; define release approval + rollback.
- [ ] **Vendor management** (CC9): build a vendor register; obtain **Azure's SOC 2**; execute and file **BAAs**; set annual vendor review.
- [ ] **Alerting + findings register** (CC4/CC7): wire alerts onto existing OpenTelemetry/Serilog; stand up a deficiency tracker.
- [ ] **Availability ops** (A1): run backups, perform a **restore test** and a **DR test**, retain after-action reports; define uptime SLA + monitoring.
- [ ] **Confidentiality** (C1): publish a **data-classification policy**; begin capturing disposal evidence.
- [ ] **Incident response** (CC7): run a **tabletop**, stand up an incident register, finalize the §164.410 breach procedure.
- [ ] Commission an **independent penetration test**; track remediation to closure.

## Phase 4 — Define the observation period (Type II)

- [ ] Confirm all in-scope controls are **demonstrably operating** (not just designed) — this is the trigger to start the clock.
- [ ] Choose the **observation window**: [DEFINE] — recommended **6 months** for a first Type II (min 3, max 12).
- [ ] Record the **window start date**; publish it to all evidence owners.
- [ ] Ensure each **recurring** control (access reviews, oversight meetings, backups, evaluations) is scheduled so at least one occurrence lands inside the window.

## Phase 5 — Gather evidence

- [ ] Turn `evidence-matrix.md` into a live tracker; mark each artifact **Ready / In progress / [TO PRODUCE]**.
- [ ] Produce the **technical exports**: audit-log export (with hash chain), CI security-scan run logs, Key Vault config, TDE proof + startup health-check log, access-review exports.
- [ ] Collect **organizational artifacts**: signed policies, risk register, minutes, training/acknowledgement logs, vendor SOC 2s, BAAs, pen-test report.
- [ ] For Type II, ensure artifacts **span the window** (samples across months, not one snapshot).
- [ ] Verify **naming, dating, and completeness** — auditors reject undated or ambiguous evidence.

## Phase 6 — Continuous monitoring & access reviews (keep running through the window)

- [ ] Access reviews performed on cadence with sign-off — **every** scheduled cycle in the window.
- [ ] Management security reviews held with minutes — every scheduled cycle.
- [ ] Alerts triaged; incidents logged and resolved; post-mortems written.
- [ ] Backups run + at least one restore/DR test evidenced.
- [ ] Audit-chain verification job runs and passes.
- [ ] CI security scans run on every change; findings tracked to closure.
- [ ] Deficiency register kept current; no control silently lapses (a single missed cycle is a Type II exception).

## Phase 7 — Type I examination (recommended interim milestone)

- [ ] Engage the selected auditor (see `auditor-rfp-and-selection.md`); sign the engagement letter.
- [ ] Provide the system description, control matrix, and design evidence.
- [ ] Support fieldwork: walkthroughs, control-design interviews, config inspections.
- [ ] Remediate any **design** findings before the Type II window matures.
- [ ] Receive the **Type I report** (design + in-place, as of a date) — share with customers under NDA.

## Phase 8 — Type II examination

- [ ] Confirm the observation window has fully elapsed with continuous evidence.
- [ ] Deliver period-sampled evidence per `evidence-matrix.md`.
- [ ] Support fieldwork: the auditor **samples** across the window and tests operating effectiveness.
- [ ] Address any **exceptions** (management response drafted for the report where needed).
- [ ] Receive the **Type II report** (operating effectiveness over the period).

## Phase 9 — Sustain (annual cycle)

- [ ] Treat SOC 2 as continuous: the next Type II window begins where the last ended (avoid gaps).
- [ ] Refresh the risk assessment annually; re-ratify policies; keep vendor reviews current.
- [ ] Feed audit findings back into control improvements.

---

### Sequencing note

**Type I is optional but recommended** as an interim: it gets a report to customers ~6 months sooner and de-risks the design before you spend a 6-month window operating controls. If a customer only accepts Type II, you can skip Type I — but you still must operate controls for the full window either way, so Type I is nearly free insurance.
