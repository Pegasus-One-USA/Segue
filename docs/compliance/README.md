# FHIRBridge Compliance Program

This folder is the **HIPAA + SOC 2 compliance program** for FHIRBridge — a multi-source FHIR/HL7 integration platform (.NET 9, SQL Server, Azure Key Vault, deployed via docker-compose) that processes ePHI as a **Business Associate**.

> **What this program is — and what it is not.** The FHIRBridge codebase implements **technical safeguards** (encryption, access control, authentication, MFA, audit logging, de-identification, rate limiting, secret management). Those are necessary but **never sufficient** on their own. HIPAA compliance and a SOC 2 report additionally require **organizational controls** that live in documents and human processes — signed BAAs, a formal risk analysis, adopted policies, workforce training, a tested contingency plan, incident/breach procedures, penetration testing, and an independent auditor's attestation.
>
> **These documents carry that organizational layer as far as documents can.** They are drafted with FHIRBridge's real technical facts pre-filled. Every place that needs a human decision, a legal signature, an assigned owner, or a purchase is explicitly marked `[ORGANIZATION TO COMPLETE]`, `[TO ASSIGN]`, `[TO CONFIRM]`, or `[ORGANIZATION TO SIGN]`. **Claude cannot sign a BAA, run the risk-assessment workshop, perform the penetration test, or issue the SOC 2 report** — those require your people, counsel, and contracted third parties.

## Program contents

| Area | Location | What it is | What YOU must still do |
|------|----------|-----------|------------------------|
| **Control mapping** | [hipaa-soc2-control-matrix.md](./hipaa-soc2-control-matrix.md) | HIPAA §164.312 + SOC 2 TSC mapped to implemented features | Hand to auditor as the technical baseline |
| **Risk analysis** (§164.308(a)(1)) | [risk-assessment/](./risk-assessment/) | Asset inventory, 30-entry threat/vuln register, treatment plan | Ratify ratings, assign owners/dates, run annually |
| **Policies** (§164.308/310/312) | [policies/](./policies/) | 15 adopted policy documents incl. breach notification | Fill org values, approve via leadership, operate |
| **Business Associate Agreements** | [baa/](./baa/) | Signable BAA template + subprocessor register | Legal review, execute with Azure & downstream + customers |
| **Penetration testing** | [pen-test/](./pen-test/) | Scope/ROE, test plan, RFP, remediation tracker | Sign ROE, hire a firm, run against staging, remediate |
| **SOC 2 readiness** | [soc2/](./soc2/) | Readiness gap assessment, evidence matrix, audit prep, auditor RFP | Remediate gaps, engage a CPA firm, run Type I → II |
| **Backup / DR** | [backup-disaster-recovery.md](./backup-disaster-recovery.md) | DR runbook, RPO/RTO, restore procedure | Implement in infra (Azure Backup, SQL jobs) + test |

## Recommended execution roadmap

Compliance is sequenced — you cannot get a meaningful SOC 2 Type II without the policies, risk analysis, and BAAs already operating. Suggested order:

1. **Assign ownership (week 1).** Designate the **Security Official** and **Privacy Official** (POL-002). Nothing else is real until someone owns it.
2. **Adopt policies (weeks 1–3).** Complete and approve the [policies/](./policies/) set through leadership. This is the paper spine of both HIPAA and SOC 2 CC1.
3. **Complete the risk analysis (weeks 2–4).** Work through [risk-assessment/](./risk-assessment/); ratify the register and open the treatment-plan actions. HIPAA §164.308(a)(1) is the single most-cited control in OCR enforcement.
4. **Execute BAAs (weeks 2–6, parallel).** Get the Microsoft/Azure BAA and every downstream subprocessor BAA in place; sign customer-facing BAAs. Track in [baa/subprocessor-register.md](./baa/subprocessor-register.md).
5. **Implement backup/DR (weeks 3–8).** Ops implements [backup-disaster-recovery.md](./backup-disaster-recovery.md) and runs a restore test.
6. **Penetration test (weeks 6–10).** Sign the ROE, engage a firm against a **synthetic-data staging** environment, remediate via the tracker.
7. **SOC 2 readiness → Type I → Type II (months 3–14).** Close [soc2/readiness-assessment.md](./soc2/readiness-assessment.md) gaps, engage a CPA firm, get a Type I, then run the 3–12 month observation window for Type II.

HIPAA has no certificate — you achieve and maintain "compliance" by operating the above and being able to prove it. SOC 2 produces an auditor's report at the end of step 7.

## Ownership (RACI — [ORGANIZATION TO COMPLETE])

| Activity | Responsible | Accountable | Consulted | Informed |
|----------|-------------|-------------|-----------|----------|
| Policies & risk analysis | [TO ASSIGN] | Security Official | Legal | Leadership |
| BAAs | [TO ASSIGN] | Legal / Privacy Official | Security Official | Customers |
| Backup / DR | [TO ASSIGN] Ops | Security Official | — | Leadership |
| Penetration test | [TO ASSIGN] | Security Official | Engineering | Leadership |
| SOC 2 engagement | [TO ASSIGN] | Leadership / Security Official | Auditor (CPA) | All staff |

## How the code and these docs connect

The implemented technical safeguards (see the [control matrix](./hipaa-soc2-control-matrix.md)) are the **evidence** the risk analysis, policies, and SOC 2 evidence matrix point at. When an auditor asks "how do you enforce MFA / audit access / encrypt at rest," the answer is a specific control in the codebase plus the policy that governs it. Keep this folder updated whenever the security posture of the code changes.
