# FHIRBridge Business Associate Agreement (BAA) Program

This folder contains the Business Associate Agreement (BAA) program for the FHIRBridge platform — a .NET 9 healthcare data-integration platform that receives, transforms, stores, and transmits electronic Protected Health Information (**ePHI**).

> **Important scope statement.** Signing BAAs is an **organizational control**, not something the codebase can satisfy. FHIRBridge's technical safeguards (encryption, access control, audit logging, de-identification) are necessary but do not, by themselves, discharge the contractual obligations HIPAA imposes on a Business Associate. Those obligations are met only when the required agreements are negotiated, signed, tracked, and renewed. This program provides the templates and trackers to do that; the organization must execute and operate it. These documents are **not legal advice** — have counsel review every agreement before signing.

## Why FHIRBridge needs BAAs

Under the HIPAA Privacy and Security Rules, a **Covered Entity** (a health plan, health-care clearinghouse, or health-care provider that transmits health information electronically) may disclose PHI to a **Business Associate** — a person or entity that creates, receives, maintains, or transmits PHI on the Covered Entity's behalf — only if the Covered Entity obtains "satisfactory assurances" that the Business Associate will appropriately safeguard the information. Those assurances must be documented in a written contract: the **Business Associate Agreement**.

FHIRBridge, by ingesting, mapping, storing, and routing ePHI for the health-care organizations it serves, is a **Business Associate** (and in most deployments a **Business Associate acting through subcontractors**). Two governing regulatory citations apply:

- **§164.502(e)** (Privacy Rule) — a Covered Entity may disclose PHI to a Business Associate, and a Business Associate may disclose PHI to a subcontractor, only with satisfactory assurances documented in a BAA. The assurances must "flow down" to every subcontractor that creates, receives, maintains, or transmits PHI.
- **§164.308(b)** (Security Rule) — the parallel administrative-safeguard requirement: a Business Associate must obtain satisfactory assurances, in a written contract or other arrangement, that any subcontractor that creates, receives, maintains, or transmits ePHI on its behalf will appropriately safeguard it.

The practical rule: **if an entity touches ePHI on FHIRBridge's behalf, there must be a signed BAA with that entity, and its obligations must be at least as protective as those FHIRBridge owes upstream.**

## The two directions of the agreement

BAAs are directional. FHIRBridge sits in the middle of the chain and is a party to agreements pointing in **both** directions.

| Direction | Who signs with whom | FHIRBridge's role | What it does |
|-----------|--------------------|-------------------|--------------|
| **Upstream (inbound)** | Covered Entity ⇄ FHIRBridge | FHIRBridge is the **Business Associate** | The Covered Entity (the customer — a hospital, clinic, payer, provider) requires FHIRBridge to sign a BAA before disclosing PHI to it. FHIRBridge is the receiving/serving party and promises to safeguard the data. |
| **Downstream (outbound / flow-down)** | FHIRBridge ⇄ Subprocessor | FHIRBridge is the **Business Associate (upstream party)** obtaining assurances | Before FHIRBridge lets any vendor (cloud host, email sender, monitoring, logging, etc.) create/receive/maintain/transmit ePHI, FHIRBridge must have a BAA with that vendor. The subprocessor becomes a **subcontractor** whose obligations flow down from FHIRBridge's own BAA. |

```
  Covered Entity (customer/hospital/payer)
        │   ── UPSTREAM BAA ──  (FHIRBridge = Business Associate)
        ▼
   ┌─────────────┐
   │  FHIRBridge │   (Business Associate)
   └─────────────┘
        │   ── DOWNSTREAM / FLOW-DOWN BAAs ──  (FHIRBridge = upstream party)
        ▼
  Subprocessors: Azure (SQL/Key Vault/Blob), email/SMTP sender,
  container hosting/registry, monitoring/APM, managed logging, …
```

## When a BAA is required (and when it is not)

A BAA **is required** when the entity, in performing a function or service for FHIRBridge (or for FHIRBridge's customer), **creates, receives, maintains, or transmits PHI**. "Maintains" is broad: it includes merely storing PHI — even encrypted, even if the vendor never looks at it. Persistent storage or transmission of PHI by a vendor triggers the requirement.

A BAA is **generally not required** when:

- The entity is a **mere conduit** — it only transports PHI with transient (not persistent) access, analogous to the postal service or an ISP. This exception is narrow; most cloud and SaaS vendors that store data do **not** qualify.
- The service **never touches PHI at all** — e.g., a marketing-website analytics tool that only sees non-PHI, or a billing/procurement tool that only processes FHIRBridge's own corporate data.
- The entity is a **member of the workforce** (employee/contractor under direct control), covered by workforce policies rather than a BAA.
- Disclosure is to another provider for treatment, or falls under another Privacy Rule exception (these are unusual in the FHIRBridge context).

When in doubt, treat the vendor as requiring a BAA and record the reasoning in the [subprocessor register](./subprocessor-register.md). Erring toward a BAA is the safer, standard practice.

## What a compliant BAA must contain

Every BAA in this program (see [`baa-template.md`](./baa-template.md)) must include the clauses HIPAA mandates:

1. Permitted and required uses and disclosures of PHI (bounded by the underlying service and by **minimum necessary**).
2. A prohibition on using or disclosing PHI other than as permitted by the agreement or required by law.
3. An obligation to implement appropriate safeguards (Security Rule for ePHI).
4. Reporting of any use/disclosure not permitted, plus **security incidents** and **breaches of unsecured PHI**, within the **§164.410** timeline.
5. **Flow-down**: ensuring subcontractors agree to the same restrictions and conditions.
6. Making PHI available for **access (§164.524)**, **amendment (§164.526)**, and an **accounting of disclosures (§164.528)**.
7. Making internal practices/books/records available to HHS for compliance review.
8. **Return or destruction** of PHI at termination (or extension of protections if return/destruction is infeasible).
9. **Termination for breach** of the agreement.

## Documents in this folder

- **[baa-template.md](./baa-template.md)** — A complete, standard BAA contract template with every HIPAA-required clause, party-specific fields marked (`[PARTY NAME]`, `[EFFECTIVE DATE]`, …), and a legal-review disclaimer. Usable in either direction.
- **[subprocessor-register.md](./subprocessor-register.md)** — The living vendor/subprocessor register: who touches ePHI, whether a BAA is required, and its status/owner. Pre-populated with the likely subprocessors given FHIRBridge's tech stack, all marked `[ORGANIZATION TO CONFIRM]`.
- **[baa-tracking-checklist.md](./baa-tracking-checklist.md)** — Step-by-step checklist for obtaining, tracking, renewing, and re-papering BAAs, and what to do when a subprocessor changes.

## How to use this program

1. Use `baa-template.md` as the starting draft for any BAA you need to sign in **either** direction; route it through legal before execution.
2. Complete and maintain `subprocessor-register.md` — confirm every `[ORGANIZATION TO CONFIRM]` entry, add any vendor not listed, and keep the status column current.
3. Work `baa-tracking-checklist.md` on a recurring cadence so no BAA lapses and every new subprocessor is papered **before** it touches ePHI.
