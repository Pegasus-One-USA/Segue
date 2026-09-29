# eClinicalWorks (eCW) FHIR Developer Documentation — Reference Capture

Source: https://fhir.eclinicalworks.com/ecwopendev/documentation/getting-started
Fetched: 2026-09-16 (via WebFetch, page-by-page; content below is each page's extracted summary — see note at the end on fidelity).

This is a working reference for FHIRBridge's eCW (Healow) integration — Backend Services, Provider EHR-launch, Provider Standalone, and Bulk Data ($export) — the exact flows this project has been building and debugging against the `staging-fhir.ecwcloud.com` / `FFBJCD` sandbox.

## Table of Contents

1. [Getting Started — Overview](#1-getting-started--overview)
2. [Sign Up and Manage Portal Users](#2-sign-up-and-manage-portal-users)
3. [Register Your App](#3-register-your-app)
4. [Test Your App (Sandbox)](#4-test-your-app-sandbox)
5. [Publish App to Production](#5-publish-app-to-production)
6. [Connect to eCW Customers](#6-connect-to-ecw-customers)
7. [Provider: EHR Launch (Symmetric)](#7-provider-ehr-launch-symmetric)
8. [Provider: EHR Launch (Asymmetric)](#8-provider-ehr-launch-asymmetric)
9. [Provider: Standalone Launch (Symmetric)](#9-provider-standalone-launch-symmetric)
10. [Provider: Standalone Launch (Asymmetric)](#10-provider-standalone-launch-asymmetric)
11. [Provider: CDS Hooks](#11-provider-cds-hooks)
12. [Backend: Authentication](#12-backend-authentication)
13. [Backend: Create and Enable Bulk Patient Groups](#13-backend-create-and-enable-bulk-patient-groups)
14. [Backend: Bulk Patient Access Specification](#14-backend-bulk-patient-access-specification)
15. [Token Introspection](#15-token-introspection)
16. [Terms of Service (App Developers)](#16-terms-of-service-app-developers)
17. [Terms of Subscription (eCW Customers)](#17-terms-of-subscription-ecw-customers)
18. [References](#18-references)

---

## 1. Getting Started — Overview
Source: https://fhir.eclinicalworks.com/ecwopendev/documentation/getting-started

### Core Concepts

**FHIR Definition**: "Fast Healthcare Interoperability Resources (FHIR®) standard is a standard for healthcare data exchange" that enables seamless information exchange across systems through standardized resource components like patients, labs, medications, and allergies.

**API Purpose**: Application Programming Interfaces facilitate communication between software applications, allowing standardized exchange of patient information and healthcare data across platforms.

**REST Architecture**: eClinicalWorks implements "Representational State Transfer (REST) as the basis for its API data exchange," using HTTP for information transfer.

### Launch Workflows

Two SMART on FHIR launch types:

1. **EHR Launch** – Applications launch from within an existing EHR session
2. **Standalone Launch** – Users select apps from outside the EHR environment (mobile devices, etc.)

### Authentication Methods

- **Asymmetric (Private Key JWT)**: Preferred method using asymmetric keypairs where clients register public keys in JWKS format while protecting private keys for JWT signing.
- **Symmetric (Client Secret)**: Uses pre-shared secrets with HTTP basic authentication, converting credentials (`client_id:client_secret`) to Base64 encoding for authorization headers.

### Backend Services

Automated connection method enabling "data operations, without requiring direct user interaction" through asymmetric authentication — for backend-to-EHR integration (this is FHIRBridge's `ApplicationType=Backend` / `EClinicalWorksSourceNode` path).

### Site navigation (full)

**Top nav**: Getting Started · API Documentation · App Gallery · FHIR Endpoints · Contact Us · Login · Sign Up

**Developer Portal Steps**: Sign Up and Manage Portal Users · Register Your App · Test Your App · Publish App to Production · Connect to eCW Customers

**Provider App Authorization**: EHR Launch (Symmetric) · EHR Launch (Asymmetric) · Standalone Launch (Symmetric) · Standalone Launch (Asymmetric) · CDS Hooks

**Backend App Authorization**: Backend Authentication · Create and Enable Bulk Patient Groups · Bulk Patient Access Specification

**Additional**: Token Introspection · Terms of Service · Terms of Subscription · References

---

## 2. Sign Up and Manage Portal Users
Source: https://fhir.eclinicalworks.com/ecwopendev/documentation/getting-started/guidelines/signup-and-manage-users

### Purpose
The eClinicalWorks Dev Portal serves as "a platform for third-party app developers to register apps with eCW Electronic Health Records (EHR)."

### Key Functions
Developers can utilize the portal to:
- Establish organizational developer accounts
- Manage co-developer access permissions
- Register applications
- Conduct sandbox environment testing
- Deploy apps to eCW customers

### Account Creation Process
New users begin by clicking the Sign Up button and completing a four-step registration:
1. Provide administrator contact details (email becomes the username)
2. Enter company information
3. Supply additional organizational details
4. Configure security questions and accept terms

After submission, a verification email is sent. Users confirm their account by answering a security question and creating a password.

### Managing Team Access
Existing account holders can add co-developers through the dashboard settings:
- Select "Manage User" from the gear icon menu
- Click "Add User" and enter contact information
- Optionally grant user management rights via checkbox

Co-developers receive an OTP via email to verify and establish their accounts. Admins can subsequently edit or remove team members if they possess appropriate permissions.

---

## 3. Register Your App
Source: https://fhir.eclinicalworks.com/ecwopendev/documentation/getting-started/guidelines/register-app

### App Registration Process (6 steps)

**Step 1: App Information** — Basic app details that appear in the eCW EHR app gallery listing.

**Step 2: Scope Selection** — "The app will only have access to the information within the selected Scopes" from the eCW FHIR Server.

**Step 3: Configuration Details** — Additional setup information required for app launch workflows, referencing the SMART App Launch spec from HL7.

**Step 4: Custom Parameters** — App-specific custom parameters for the integration.

**Step 5: Additional Information** — Descriptive content displayed on consent screens during standalone or EHR activation.

**Step 6: JWKS URL (Backend Apps)** — For asymmetric authorization, backend apps must provide a JWKS URL following RFC 7517.

### Next Steps
After submission, the registered app appears in the Published App section. Testing in the eCW sandbox is required before production deployment.

### Resources Referenced
- SMART App Launch scopes and launch context documentation
- JWKS specification (RFC 7517)
- FHIR confidential client asymmetric authentication guidelines

---

## 4. Test Your App (Sandbox)
Source: https://fhir.eclinicalworks.com/ecwopendev/documentation/getting-started/guidelines/sandbox-testing

### Overview
"Currently the sandbox testing is only supported for EMR launch apps."

### Testing Process (4 steps, EMR launch apps)
1. **Initiate Launch** — Click "Launch" on the registered app tile under Published App tab
2. **Select User Profile** — Choose Provider or Staff profile
3. **Choose Patient Record** — Select a patient from the available list
4. **Launch Application** — Opens the app in a separate window with the selected provider/patient context

### Important Notes
- "The provider, staff, and patient profiles are created by the eCW Dev Portal Team"
- Patient profiles have varying demographics/clinical data for different use cases
- **Postman is recommended for Standalone Provider launch testing**; backend apps require server/application-level testing
- Sandbox support is expected to extend to standalone and bulk/backend apps in the future

### Next Steps
After sandbox validation → Publish App to Production.

---

## 5. Publish App to Production
Source: https://fhir.eclinicalworks.com/ecwopendev/documentation/getting-started/guidelines/publish-app

### Publishing Process (4 steps)
1. **Access Management** — Click "Manage" on the app tile
2. **Initiate Publishing** — Click "Publish to Production" on the app detail page
3. **Enter Details** — Input production details, click "Publish"
4. **Verification** — "Published to Production" label appears on the app tile

### Important Next Steps
After publication, enable eCW customers to activate the app — see "Connect to eCW Customers" below.

---

## 6. Connect to eCW Customers
Source: https://fhir.eclinicalworks.com/ecwopendev/documentation/getting-started/guidelines/enable-ecw-cust

### Process Overview (4 steps)

**Step 1** — Developer obtains an "App Activation Code" from the Dev Portal's Production Configuration section, shares it with the customer.

**Step 2** — Customer navigates their EMR: Product Activation → FHIR APIs Settings → Provider Centric or Backend Apps dashboard → enters the activation code in "Add New App."

**Step 3** — Developer explicitly approves each customer by selecting their Practice Name and confirming access via the Production Configuration Information tab on the Dev Portal.

**Step 4** — Customer sees an "Activate" button for approved apps, proceeds to enable user access or configure patient groups/cohorts (depending on app type).

### Key Features
- Developers retain control over which customers can access their applications
- Customer access can be revoked any time (trash icon)
- Previously enabled customers can be reactivated without repeating initial setup
- Activation process differs between Provider Apps and Backend/Bulk Access Apps
- "developers should have completed the required business processes" with customers before enabling them

---

## 7. Provider: EHR Launch (Symmetric)
Source: https://fhir.eclinicalworks.com/ecwopendev/documentation/getting-started/provider/ehr-launch-symmetric

SMART App Launch Framework — EHR Launch scenario, symmetric (client-secret) authentication.

### Core Authorization Flow (8 steps)

**Steps 1–3: Launch & Authorization Request**
EHR initiates launch by opening an iframe with `iss` (FHIR endpoint) and `launch` (unique token) params. The app requests an authorization code from the EHR's authorize endpoint.

**Steps 4–5: Token Exchange**
App exchanges the authorization code for an access token via HTTP POST to the token endpoint — basic auth (confidential apps) or public-app method with PKCE.

**Step 6: Resource Access**
Access token presented as Bearer token in the Authorization header for FHIR resource requests.

**Steps 7–8: Token Refresh**
Refresh tokens (valid 90 days) obtain new access tokens without re-authorization.

### Key Technical Requirements
- **PKCE mandatory**: S256 code challenge method
- **No CORS support**: "We do not support CORS headers for the FHIR APIs"
- **Public URLs required**: "We do not support localhost url for the EHR launch workflow"
- **OpenID Connect**: ID tokens contain claims about the launching end-user

### Authentication Methods
- **Confidential apps**: Base64-encoded `client_id:client_secret` via Basic Authentication
- **Public apps**: Client ID only, no refresh token issued

Includes extensive error code tables, example requests/responses, and JWT payload structures (not fully reproduced by the summarizing fetch — see fidelity note).

---

## 8. Provider: EHR Launch (Asymmetric)
Source: https://fhir.eclinicalworks.com/ecwopendev/documentation/getting-started/provider/ehr-launch-asymmetric

### Key Implementation Steps

**Step 1 – App Launch**: EHR opens an iframe to the app's registered launch URL with:
- `iss`: FHIR endpoint for metadata retrieval
- `launch`: uniquely generated launch token

> "We do not support localhost url for the EHR launch workflow; launch and redirect url shall be public url for eCW to access."

**Step 2 – Authorization Request**: `response_type=code`, `client_id`, `redirect_uri`, `scope`, plus PKCE (`code_challenge`, `code_challenge_method=S256`).

**Step 3 – Authorization Response**: Authorization code or error response.

**Step 4 – Token Exchange (asymmetric)**:
- Generate a one-time JWT signed with the app's private key
- **RS384 algorithm exclusively**
- Include `code_verifier` for PKCE validation

**Step 5 – Token Response**:
- `access_token` (JWT)
- `refresh_token` (valid 90 days, if `offline_access` scope approved)
- `id_token` (OpenID Connect claims)

**Step 6 – FHIR Resource Request**: Bearer token per RFC 6750 §2.1.

**Steps 7–8 – Token Refresh**: New access tokens via refresh token, no re-authorization.

### Important Constraints
> "We do not support CORS headers for the FHIR APIs. Third-party app should resolve any CORS related error faced during FHIR APIs implementation."

Public keys registered via JWKS URLs; `kid` in `client_assertion` must match the registered key.

---

## 9. Provider: Standalone Launch (Symmetric)
Source: https://fhir.eclinicalworks.com/ecwopendev/documentation/getting-started/provider/standalone-symmetric

Serves both provider-centric and patient-centric apps.

### Key Authorization Flow Steps

**Step 1 – Authorization Request**: `response_type`, `client_id`, `redirect_uri`, `state`, `scope`, `aud`, `code_challenge`, `code_challenge_method`.

**Step 2 – User Authentication**: Redirect to eClinicalWorks login (providers) or Healow login (patients).

**Step 3 – Authorization Code Response**: Authorization code + `state`.

**Step 4 – Token Exchange**: Public app flow (no client secret) or confidential app flow (Basic auth).

**Step 5 – Token Response**: `access_token`, `token_type`, `expires_in`, `scope`, optional `refresh_token`, `id_token`, `smart_style_url`.

**Steps 6–8 – Resource Access & Refresh**: Bearer token; refresh tokens on expiry.

### Critical Parameters
> "The app must include the space delimited list of scopes that the app wants to access."

Refresh tokens valid 90 days.

### Error Handling
| Error | Code |
|---|---|
| invalid_client | 401 |
| unsupported_grant_type | 400 |
| invalid_grant | 400 |
| invalid_request | 400 |
| access_denied | 500 |

---

## 10. Provider: Standalone Launch (Asymmetric)
Source: https://fhir.eclinicalworks.com/ecwopendev/documentation/getting-started/provider/standalone-asymmetric

### Key Application Types
- **Provider-centric apps** (medical professionals)
- **Patient-centric apps** (patients)

### Authentication Methods
- **Symmetric** — public clients
- **Asymmetric** — JWT with RS384, for confidential apps

### Core Process Flow (8 steps)

**Steps 1–3**: Authorization request (`client_id`, `redirect_uri`, `scope`, PKCE `code_challenge`/`code_challenge_method: S256`) → redirect to eCW/Healow login → authorization code returned.

**Steps 4–5**: Token exchange via POST; asymmetric auth requires JWT client assertion. Response: `access_token`, `refresh_token`, `id_token`, `expires_in`.

**Step 6**: Resource access — `GET https://{fhir_base_url}/[FHIR-Resource]?{search-parameters}` with Bearer token.

**Steps 7–8**: Refresh tokens (valid 90 days) via `grant_type: refresh_token`.

### Notable Limitations
> "We do not support CORS headers for the FHIR APIs."

---

## 11. Provider: CDS Hooks
Source: https://fhir.eclinicalworks.com/ecwopendev/documentation/getting-started/provider/cds-hooks

### Overview
Two primary components:
1. **Service API** — CDS Clients request decision support
2. **Feedback API** — clients report outcomes of recommendations

All communication requires HTTPS/TLS (RFC 2818).

### Authentication Options
- **No authentication** — open endpoints
- **Access Token** — client-credentials grant, Bearer token
- **JWT** — specific header/payload requirements

### Request/Response Flow

**Step 1: CDS Service Call** — POST JSON including:
- `hook` — triggering event (`encounter-start`, `order-select`, etc.)
- `hookInstance` — UUID for the call
- `fhirserver` — base FHIR Server URL
- `context` — hook-specific data
- `prefetch` — pre-fetched FHIR resources

**Step 2: Service Response** — HTTP 200 with a `cards` array (summary, indicator: info/warning/critical, suggestions).

**Step 3: Feedback** — POST with outcome status + ISO 8601 timestamp for card acceptance/rejection.

### Key HTTP Status Codes
- 200 OK — success
- 412 Precondition Failed — cannot retrieve necessary FHIR data
- 500 — processing errors

---

## 12. Backend: Authentication
Source: https://fhir.eclinicalworks.com/ecwopendev/documentation/getting-started/backend/authentication

Backend services connect to eCW's FHIR server without direct user interaction: register a public key, request an access token, then initiate bulk data requests.

### Registration
> "The app registers the public key that the client uses to authenticate itself to the eCW FHIR Authorization Server on the eCW Dev Portal."

### Token Request
Generate a JWT (RS384), then POST to the token endpoint with:
- `grant_type`: `client_credentials`
- `scope`: space-delimited (e.g. `system/Patient.read system/Group.read`)
- `client_assertion_type`: `urn:ietf:params:oauth:client-assertion-type:jwt-bearer`
- `client_assertion`: JWT signed with the app's private key

**Important scope note**:
> "system/Group.read scope must be included in all token requests for the bulk data (Group resource) requests."

This directly confirms the FHIRBridge fix applied this session — Group $export always needs `system/Group.read` regardless of which resource types are requested.

### Token Response
- `access_token` — JWT
- `token_type` — "Bearer"
- `expires_in` — lifetime in seconds
- `scope` — approved scopes

### Common Errors
| Error | Code | Cause |
|---|---|---|
| invalid_client | 401 | Missing/mismatched client_id, invalid `kid`, or inaccessible JWKS URL |
| unsupported_grant_type | 400 | `grant_type` other than `client_credentials` |
| invalid_grant | 400 | Unsupported or malformed scope |

### Additional Notes
> "We do not support CORS headers for the FHIR APIs."

Applications must obtain the token URL via a metadata call to the issuer endpoint.

---

## 13. Backend: Create and Enable Bulk Patient Groups
Source: https://fhir.eclinicalworks.com/ecwopendev/documentation/getting-started/backend/patient-groups

### Creating Groups
Groups = "groups of patients satisfying clinical conditions created by a practice."
- Main Menu > Registry > Registry
- Select patient demographics and clinical conditions
- Save queries, optionally designating them as groups for backend apps
- Receive an auto-generated **group ID**

This is exactly the "Bulk Group Details" screen referenced throughout this project's eCW memory — the practice-side UI where valid Group IDs (like `61523f95-169b-4ac5-bb06-3666b9cd944e`) come from.

### Enabling Groups
Admin > Product Activation > FHIR APIs > Bulk/Backend Apps — move groups from "Available Groups" to "Enabled Groups," which lets applications access patient data within approved scopes.

### Reviewing/Updating
Main menu > Registry > Registry > Saved Reports — run saved reports to update group membership and confirm third-party app access.

---

## 14. Backend: Bulk Patient Access Specification
Source: https://fhir.eclinicalworks.com/ecwopendev/documentation/getting-started/backend/patient-access

### Core Purpose
FHIR Bulk Data Access specification ("Flat FHIR"), HL7 STU1.0.1.

### Key Requirements
- Apps must be authorized for bulk access APIs and system scopes
- eCW EMR organizations must authorize app access to patient cohorts/groups

### Version Support
- USCDI v3: 12.0.2, 12.0.3
- USCDI v1: 11.52.305C through 12.0.3

### Primary Workflow

**1. Kick-off Request**
```
GET https://{fhir_base_url}/org_id/Group/{group_id}/$export
```
Required headers:
- `Prefer: respond-async`
- `Accept: application/fhir+json`
- Authorization bearer token

Optional params: `_type` (resource filtering), `_since` (date filtering), `_outputFormat`.

**2. Status Monitoring**
```
GET https://{fhir_base_url}/org_id/$export-poll-location?job_id={job_id}
```
Responses progress 202 (Accepted/In-Progress) → 200 (Complete).

**3. Data Retrieval**
Download NDJSON files from the manifest's provided URLs.

### Resource Types Supported
Clinical (Condition, Goal, CarePlan), referenced (Practitioner, Location, Organization), administrative — with special handling noted for CareTeam and DocumentReference.

### HTTP Status Codes
- 401 — invalid/expired token or unauthorized group
- 404 — invalid JobId
- 202 — job accepted/processing
- 200 — completion

---

## 15. Token Introspection
Source: https://fhir.eclinicalworks.com/ecwopendev/documentation/getting-started/token-introspection

OAuth 2.0 token introspection for third-party developers.

### Request Phase
> "The app performs HTTP POST request with parameters sent as 'application/x-www-form-urlencoded'."
Confidential clients must use Basic auth (base64-encoded credentials).

### Response Phase
JSON containing:
- `active` — boolean, token validity
- `scope` — granted permissions
- `client_id` — application identifier
- `exp` — expiration timestamp (integer)

### Example
```
POST https://{ehr_token_url} HTTP/1.1
Authorization: Basic czZCaGRSa3F0MzpnWDFmQmF0M2JW

token=SlAV32hkKG
```

Valid tokens → HTTP 200 with `active: true` + metadata. Invalid tokens → `{"active": false}`.

> Note from this project's own eCW memory: introspection has been observed returning `active: false` even for tokens that work fine for actual FHIR calls — treat only `active: true` as meaningful, per [[ecw-backend-bulk-poc]].

---

## 16. Terms of Service (App Developers)
Source: https://fhir.eclinicalworks.com/ecwopendev/documentation/getting-started/terms-of-service

Standard developer agreement governing FHIR API access.

- **License Grant**: "limited, non-exclusive, non-assignable, non-transferable, non-sublicensable license."
- **Ownership**: eCW retains all IP; no derivatives without permission.
- **Data Requirements**: apps "must use USCDI as a technical requirement"; HIPAA and data-protection law compliance required.
- **Security Obligations**: maintain security programs, report breaches within 24 hours, undergo authenticity verification, regular vulnerability testing.
- **Prohibited Uses**: no spyware/malicious code, no unlicensed drugs/weapons/gambling, no unlawful use; data cannot be sold or used for marketing without consent.
- **Liability Limits**: highest of (amounts paid in prior 3 months, $100, or legal minimum).
- **Termination**: either party, 30 days' notice; eCW may terminate immediately for patient-safety risk or legal violation.
- **Dispute Resolution**: binding arbitration in Boston, MA, under AAA rules.

---

## 17. Terms of Subscription (eCW Customers)
Source: https://fhir.eclinicalworks.com/ecwopendev/documentation/getting-started/terms-of-subscription

Legal agreement governing healthcare organizations' use of eCW's FHIR APIs.

- **License Grant**: non-exclusive license to use Healow/eCW FHIR APIs, contingent on active eCW software licenses.
- **Customer Responsibilities**: vet third-party apps, verify data accuracy, maintain security, backups, keep software current. "the Customer is responsible for the compliance of any Product...with applicable laws and regulations."
- **Support Limitations**: eCW does NOT troubleshoot third-party app issues, data corruption, security vulnerabilities, or integration failures.
- **Fee Structure**: via eCW's certified EHR technology portal; 30 days' notice before fee changes; customer may terminate with 10 days' notice on pricing disagreement.
- **Liability Caps**: highest of (12 months of API fees paid, $100, legal minimum); consequential damages excluded.
- **Termination**: auto-ends if customer stops being an eCW licensee; either party may terminate for material breach after 60 days' notice + cure opportunity.
- **Governing Law**: Massachusetts.

---

## 18. References
Source: https://fhir.eclinicalworks.com/ecwopendev/documentation/getting-started/references

External standards cited by the eCW documentation:

1. **SMART App Launch** — HL7 FHIR STU2 specs covering backend services, asymmetric/symmetric authentication, token introspection.
2. **PKCE** — RFC 7636.
3. **FHIR US Core IG** — USCDI v1 (STU3.1.1) and USCDI v3 (STU6.1).
4. **FHIR R4** — standard implementation guide.
5. **Bulk Access IG** — HL7 FHIR bulk data export spec, STU1.0.1.

---

## Fidelity note

Each section above was captured via an AI-summarizing web fetch (page → markdown → small-model extraction), not a raw HTML dump — so it is a **faithful but condensed** capture of each page's structure, key facts, exact quotes, and endpoint/parameter names, not a byte-for-byte copy. Sections 7–14 (the actual auth-flow pages) are the ones most likely to have additional example JSON payloads, full JWT claim tables, and complete error-code tables on the live pages beyond what's captured here — worth a direct re-read of the live page if you need the exact wire-format example for a specific step.
