# BAA Tracking & Maintenance Checklist

An operational checklist for obtaining, tracking, renewing, and re-papering Business Associate Agreements for FHIRBridge. A BAA is not a one-time task — it must stay current for the entire life of every data relationship. Assign an owner (Privacy/Security Officer or delegate) and run the recurring items on schedule.

> Use this alongside [`README.md`](./README.md) (why/direction/when), [`baa-template.md`](./baa-template.md) (the contract), and [`subprocessor-register.md`](./subprocessor-register.md) (the tracker of record).

---

## A. Obtaining an UPSTREAM BAA (customer / Covered Entity → FHIRBridge)

- [ ] Identify each Covered Entity customer that will disclose PHI to FHIRBridge.
- [ ] Determine who supplies the paper: the customer's BAA form or FHIRBridge's template. If FHIRBridge's, start from `baa-template.md`.
- [ ] Route the draft through **legal counsel** before signing (see disclaimer in the template).
- [ ] Confirm the agreement includes all required clauses: permitted uses/disclosures, safeguards, incident/breach reporting with the §164.410 timeline, subcontractor flow-down, access/amendment/accounting (§164.524/526/528), return-or-destruction, termination for breach, minimum necessary.
- [ ] Verify FHIRBridge can actually meet every promised obligation (e.g., breach-notice timeline, access/amendment turnaround) operationally — do not sign to terms the platform/ops cannot deliver.
- [ ] Execute and store the signed copy in the central BAA repository (see Section E).
- [ ] Record: parties, effective date, renewal/term, notice contacts, and any obligation shorter/stricter than the template default.
- [ ] Ensure **no PHI is accepted from the customer before the BAA is signed.**

## B. Obtaining a DOWNSTREAM / flow-down BAA (FHIRBridge → subprocessor)

- [ ] For every vendor in the [subprocessor register](./subprocessor-register.md), confirm whether it creates/receives/maintains/transmits ePHI. When in doubt, treat it as YES and document the reasoning.
- [ ] For each vendor requiring a BAA, obtain and sign one **before** it processes any ePHI:
  - [ ] For **Azure**, confirm the Microsoft Online Services DPA/BAA is in effect for the tenant and that **every Azure service actually used** is on Microsoft's BAA-covered-services list.
  - [ ] For **email/SMTP, monitoring/APM, logging**, request the vendor's BAA (or confirm PHI is designed out of that channel and record that decision).
- [ ] Confirm the subprocessor's obligations are **at least as protective** as FHIRBridge's upstream BAAs (flow-down, §164.308(b)(2) / §164.502(e)(1)(ii)).
- [ ] Update the register: status → `REQUESTED` when sent, `SIGNED` with date when executed; assign an Owner.
- [ ] Store the signed copy centrally.

## C. Renewal & ongoing tracking (recurring)

- [ ] Maintain a single source of truth (the subprocessor register + a signed-agreements repository) with, for each BAA: parties, direction, effective date, term/renewal date, auto-renew flag, notice period, and owner.
- [ ] **Quarterly:** review the register — verify statuses, chase anything `NOT STARTED`/`REQUESTED`, confirm no vendor is processing ePHI without a signed BAA.
- [ ] **90 / 60 / 30 days before any term/renewal date:** set reminders; confirm the BAA auto-renews or initiate renewal.
- [ ] **At least annually:** confirm each vendor still performs the same service and touches ePHI the same way; re-confirm Azure covered-services coverage; re-confirm email/logging PHI-exclusion decisions still hold.
- [ ] Keep notice contacts (Exhibit B) current on both sides.
- [ ] Retain expired/terminated BAAs per the retention requirement (HIPAA: **6 years** from the later of creation or last-in-effect date; align with the org's 7-year retention practice).

## D. When a subprocessor changes (trigger-based)

Run this whenever a vendor is **added, removed, replaced, or changes what it does with data**.

- [ ] **New subprocessor / new service that touches ePHI:** add a register row; complete Section B **before** it goes live; check whether upstream customer BAAs require **advance notice or consent** for new subprocessors and honor that.
- [ ] **Existing vendor's scope changes** (e.g., a monitoring or email tool starts carrying PHI, or Seq is promoted from dev to a prod path): re-evaluate "Touches ePHI?" and "BAA required?"; obtain/amend the BAA accordingly.
- [ ] **Vendor replaced/removed:** trigger the terminating vendor's **return-or-destruction** obligation (§ 6.4 of the template); obtain written certification of destruction; update the register (status/owner/date) and, if the vendor was disclosed to customers, notify them per their BAA terms.
- [ ] **Vendor breach or non-compliance:** invoke mitigation, breach-reporting to affected upstream Covered Entities within the agreed/§164.410 timeline, and the termination-for-breach clause if uncured.
- [ ] **Regulatory change:** if HIPAA requirements change, amend affected BAAs (template § 7.2) and re-execute as needed.

## E. Central repository & evidence (for audit)

- [ ] Store all executed BAAs (both directions) in a single access-controlled location; keep the register linked to each signed file.
- [ ] Keep an audit trail: who signed, when, and which template version.
- [ ] Be able to produce, on request, a complete list of upstream customers and downstream subprocessors with current BAA status — this is standard evidence for HIPAA/SOC 2 reviews.
- [ ] Confirm no `[ORGANIZATION TO CONFIRM]` / `[TO ASSIGN]` placeholders remain in the register for any production vendor.

---

**Owner:** `[TO ASSIGN — Privacy/Security Officer]` · **Review cadence:** quarterly (+ event-triggered) · **Last reviewed:** `[DATE]`
