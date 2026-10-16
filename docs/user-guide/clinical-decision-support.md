# Clinical Decision Support — admin guide

This guide shows how to set up Clinical Decision Support in Segue, connect an EHR to it, and try it out with the
free public CDS Hooks sandbox or with Segue's own Test screen.

Technical background: [22 — CDS Hooks](../backend/22-cds-hooks.md).

## The idea in one minute

- While a clinician works in the EHR, the EHR can ask Segue for advice at set moments: when a **patient chart is
  opened**, when an **order is selected**, and when an **order is signed**.
- Segue answers within a second or two with **cards**: short notes such as "Avoid metformin: eGFR 25 is below 30".
  Each card has a level: **Info**, **Warning** or **Critical**.
- Segue **does not keep any patient data** from these calls. It reads what the EHR sends, builds the cards, and
  forgets it. It records only that a call happened and how it went.
- If the EHR leaves out data a check needs, Segue can read it from the EHR's own FHIR server for that one call,
  using the short-lived access the EHR sends with the call. Nothing it reads is kept.
- A card can also offer **one-click fixes**: buttons that remove the drafted order, add a different medicine or
  test, or add a problem or diagnosis. They change only the clinician's **unsigned drafts**; the clinician still
  signs. Fixes are **off** until you turn them on.
- The standard the EHR and Segue use to talk is called **CDS Hooks** (version 2.0). Epic uses it too, with a few
  extras; Segue understands them.
- **The clinical rules that come with Segue are sample data.** Your clinical team must review them before the
  service is used with real patients. Every card says so.

## Words used in this guide

| Word | Meaning |
|---|---|
| **Check** | The kind of rule a service runs, for example "Drug–allergy check". Segue has five. |
| **Service** | One check set up with your own name and settings. You can add the same check more than once with different settings. |
| **Runs when** | The moment in the EHR that calls the service: **Patient chart opened**, **Order selected** or **Order signed**. |
| **Card** | One piece of advice the EHR shows the clinician. |
| **Trusted issuer** | An EHR that Segue accepts calls from. Each EHR signs its calls; Segue answers only signed calls from issuers on its list. |
| **Issuer (iss)** | The name an EHR puts on its signed calls, usually a web address. Your EHR team gives it to you. |
| **JWKS URL** | The web address where an EHR publishes the public keys Segue uses to check its signature. It must start with `https://`. |
| **Discovery URL** | The one web address you give the EHR. From it the EHR finds every service that is on. |
| **Service URL** | The web address of one service. The EHR calls it directly. |
| **Sandbox** | The public CDS Hooks sandbox at https://sandbox.cds-hooks.org, a free pretend EHR for trying services. |
| **Client id (sub)** | For Epic, the id the health system gave Segue in its Epic build. When you enter it, Segue accepts only calls carrying that id. |
| **Key set address (jku)** | Epic writes the address of its public keys into every call. Segue accepts the call only when that address is exactly the issuer's **JWKS URL**. |
| **EHR FHIR server** | The address of the EHR's FHIR data. Segue reads missing data only from this address. For Epic it is the same as the issuer. |
| **One-click fix** | A button on a card that changes the clinician's drafts when clicked: removes a draft order, adds an order, or adds a problem or diagnosis. |
| **Dismiss reason** | A reason the clinician can pick when they dismiss a card, such as "Will monitor the patient". |
| **Card link** | A button on a card that opens a web page or an app inside the EHR (a SMART app). |
| **Epic setup sheet** | Everything Epic's team must type into Epic for one service, on one page you can copy or download. |

## The five checks

| Check | Runs when | What it does |
|---|---|---|
| **Drug–allergy check** | Order selected, Order signed | Warns when a drafted medicine matches one of the patient's recorded allergies, by ingredient or drug class. |
| **Drug–drug interaction check** | Order selected, Order signed | Warns when a drafted medicine interacts with one the patient already takes, or with another drafted order. |
| **Renal dose check** | Order selected, Order signed | Flags drafted medicines that need a dose review, or should be avoided, at the patient's latest kidney function (eGFR). You set the eGFR threshold. |
| **Care-gap reminders** | Patient chart opened | Reminds about overdue care: HbA1c for patients with diabetes, flu vaccine, colorectal cancer screening. You set the intervals and ages. |
| **Prior-authorization reminder** | Order signed | Reminds the clinician when an order is on your list of codes that need prior authorization. You keep the list. |

## Where to find it

Click **Clinical Decision Support** in the left-hand menu. You need the **CDS Hooks** permission **View**
(`cdshooks.view`). To grant it, open **User Management → Role Permissions**; the row is **CDS Hooks** under
**Clinical Decision Support**, with View, Create, Edit, Delete and Test.

## The five screens

The section has four tabs at the top, plus the service setup page.

### 1. Services

The list of your services: name and service id, the check it runs, when it runs, an **On** switch, and when it
was last changed.

- The **On** switch saves straight away. When a service is off, EHRs can't call it and it is not listed for them.
- A yellow **Changes EHR drafts** label means the service's cards offer one-click fixes.
- The **⋮** menu on each row has **Edit** (or **View** if you may not change it), **Test** and **Delete**.
- **Add service** opens a window where you pick the check.

### 2. Service setup

Opens when you add a service or click **Edit**. It has these parts:

- **Basics**: the check (fixed once the service is created), **Runs when**, **Name**, **Service id** and
  **Description**. The service id is filled in from the name until you change it. It becomes part of the web
  address, so don't change it after an EHR is set up. The **Service URL** is shown below it with a copy button.
- **Settings**: the check's own settings, such as the eGFR threshold or the prior-auth codes. Each has a default.
- **Card source**: the name (and optional link) shown on every card as where the advice comes from.
- **Who can call it**: tick trusted issuers to allow only those. Leave all unticked to allow any trusted issuer
  that is on.
- **Data the EHR sends**: what the EHR sends with each call for this check. Segue uses it only to build the cards.
  - **Fetch missing data from the EHR** (on by default): if the EHR leaves some of it out, Segue reads it from the
    EHR's FHIR server. The answer must still come back in about a second, so a slow EHR means fewer cards. The
    trusted issuer must allow this too.
  - The **Epic setup sheet** link (top right) opens this service's setup sheet on **Connect your EHR**.
- **One-click fixes**: off by default. See [Turn on one-click fixes safely](#turn-on-one-click-fixes-safely).
- **Dismiss reasons**: the reasons a clinician can pick when dismissing a card. Up to 10. See
  [Change the dismiss reasons](#change-the-dismiss-reasons).
- **Card links**: buttons on every card that open a web page or a SMART app. Up to 10. See
  [Add card links](#add-card-links).

### 3. Connect your EHR

- **Discovery URL** with a copy button. Give it to your EHR team. If a yellow warning says the address was worked
  out from the page you are on, ask your administrator to set `Cds:PublicBaseUrl` to the address EHRs really use.
  Until it is set, Segue refuses EHR calls (they show as **Not signed in**), so it cannot be fooled about which
  address a call was meant for.
- **For the EHR team**: short steps to pass on.
- **Service URLs**: each service's own address.
- **Trusted issuers**: the EHRs Segue accepts calls from. **Add issuer** opens a form: **Name**, **EHR vendor**,
  **Issuer (iss)**, **JWKS URL** *or* pasted keys, an optional **Audience (aud) override**, **Client id (sub)**,
  **EHR FHIR server**, **Let Segue fetch missing data from this EHR**, and **On**. With no trusted issuers, every
  EHR call is refused. The list shows a **Missing data** column (whether Segue may read missing data from that EHR)
  and a **Client id checked** note. The **⋮** menu has **Test callback**, which checks that Segue can reach the
  EHR's FHIR server (it sends no patient data and no access token).
- **Epic setup sheet**: pick a service to see every value Epic's team must enter, with **Copy** and
  **Download JSON** buttons. See [Hand the Epic setup sheet to Epic](#step-3-hand-the-epic-setup-sheet-to-epic).
- **Try it with the public CDS Hooks sandbox**: the sandbox's values and an **Add sandbox as trusted issuer**
  button.

### 4. Test

Pick a service, load a ready-made sample request, change it if you like, and click **Run**. The cards appear as
an EHR would show them. **Show raw JSON** shows the exact answer and the settings used. What you paste here is not
stored and is not written to Activity. Use made-up patients only. You need **View** and **Test**.

- If the service has fixes on, each card shows its fixes as buttons you can tick. **Recommended** ones are ticked
  first. Ticking one shows exactly what the EHR would add or remove; **Resource sent to the EHR** shows the data.
  Nothing is sent to any EHR from this screen.
- Card links show as buttons under the card.
- The Test screen never reads missing data from an EHR. If the sample is missing something, the card list is
  empty and the note says so.

### 5. Activity

Every call an EHR made, newest first: when, calls or feedback, which service and hook, who called, the outcome,
the HTTP status, how many cards, how long it took, and a short reason. It never shows patient data. Filter by
service, type, outcome and dates; **Reset** clears the filters.

Small labels under a row add detail when there is any:

| Label | Meaning |
|---|---|
| **Epic trigger 18** | The Epic action that fired the alert. |
| **Epic 11.2** / **Criteria 12345** | The Epic version, and the Epic criteria record that fired the alert. |
| **Fetched 3 from EHR in 240 ms** | Data the EHR left out, read from its FHIR server for this call. Nothing was stored. |
| **2 fixes offered** / **1 fix accepted** | One-click fixes on the cards, and fixes the clinician clicked (from feedback). |
| **Dismissed: will-monitor** | The dismiss reason code the clinician picked. Epic may send its own number code instead. |

A line above the table counts fixes offered, fixes accepted, and each dismiss reason on the current page.

Outcomes you may see:

| Outcome | Meaning |
|---|---|
| **Cards returned** | The service answered with advice. |
| **No cards** | The service had nothing to say. This is normal. |
| **Missing data** | The EHR did not send all the data the check needs and Segue could not read it from the EHR either, or the check ran out of time. Segue answered with no cards. **Detail** says why, for example "callbacks are off for this trusted issuer" or "callback: timed out". |
| **Missing data (412)** | The EHR sent neither the data nor the address of a FHIR server to fetch it from. Segue refused the call. |
| **Not signed in** | The call's signature was missing or wrong, the issuer is not trusted or is off, or the token was already used. See **Detail**. |
| **Not allowed** | The issuer is trusted but not ticked under **Who can call it** for this service. |
| **Unknown service** | No service with that id is on. |
| **Bad request** / **Error** | The call was malformed, or the check failed. |

## Try it with the Test screen (no EHR needed)

1. Click **Clinical Decision Support** in the left-hand menu.
2. Click **Add service**.
3. Click **Renal dose check**.
4. In **Name**, type `Renal dose test`.
5. Click **Add service** at the bottom of the page.
6. Click the **Test** tab.
7. Click the **Service** box.
8. Click **Renal dose test**.
9. Click **Load sample request**.
10. Click **Run**.
11. A red **Critical** card appears: avoid metformin, eGFR 25.
12. In the request, change `"value": 25` to `"value": 80`.
13. Click **Run**. No card appears; "The service ran and had no advice" is the normal answer.

You can do the same with the other four checks. Each sample request is made up so that it produces at least one
card.

## Try it with the public CDS Hooks sandbox

The sandbox at https://sandbox.cds-hooks.org is a pretend EHR that runs **in your web browser**. Your browser, not
a server, calls Segue. So:

- Segue's API must be reachable from the computer you are using. On a local setup, `http://localhost:5000` works
  when the browser is on the same machine.
- Use Chrome or Edge. If the browser asks to let the site access devices on your local network, click **Allow**.
- The sandbox has made-up patients only.

### Step 1: trust the sandbox

1. Click **Clinical Decision Support** in the left-hand menu.
2. Click the **Connect your EHR** tab.
3. In the box **Try it with the public CDS Hooks sandbox**, click **Add sandbox as trusted issuer**.
4. A form opens already filled in: Issuer `https://sandbox.cds-hooks.org` and JWKS URL
   `https://sandbox.cds-hooks.org/.well-known/jwks.json`.
5. Click **Add issuer**.
6. The box now says **Sandbox already trusted**.

If you prefer to type it yourself: click **Add issuer**, enter Name `CDS Hooks sandbox`, EHR vendor `Other`,
Issuer (iss) `https://sandbox.cds-hooks.org`, JWKS URL `https://sandbox.cds-hooks.org/.well-known/jwks.json`,
leave the audience blank, keep **On** ticked, and click **Add issuer**.

### Step 2: add a service

1. Click the **Services** tab.
2. Click **Add service**.
3. Click **Care-gap reminders**.
4. In **Name**, type `Care gaps`.
5. Click **Add service** at the bottom of the page.

For an order check, add a second service with **Drug–allergy check** or **Renal dose check** the same way.

### Step 3: give the sandbox the discovery URL

1. Click the **Connect your EHR** tab.
2. Next to the **Discovery URL** (for example `http://localhost:5000/cds-services`), click the copy button.
3. Open https://sandbox.cds-hooks.org in a new browser tab.
4. Open the sandbox's settings menu (top right).
5. Click **Add CDS Services**.
6. Paste the discovery URL.
7. Click **Save**.

### Step 4: see the cards

1. In the sandbox, stay on the patient view (the first screen). It calls your **Care gaps** service.
2. Cards from Segue appear on the right, each marked with your card source name.
3. For an order check, open the sandbox's medication ordering screen and pick a medicine. Your drug-allergy or
   renal-dose service is called.

What you see depends on the sandbox patient's data, so "no cards" can be a correct answer. The sandbox orders
medicines only, so the **Prior-authorization reminder** (which looks at procedure codes) is best tried on the
Test screen.

### Step 5: check Activity

1. In Segue, click the **Activity** tab.
2. Each sandbox call is listed with its outcome. If cards didn't appear, look at **Outcome** and **Detail**:
   - **Not signed in** with "aud ... does not match" or "Cds:PublicBaseUrl is not set": the sandbox called a
     different address from the one Segue expects, or none is configured. Ask your administrator to set
     `Cds:PublicBaseUrl`.
   - **Not signed in** with "not an enabled trusted CDS client": the sandbox issuer is missing or off. Redo Step 1.
   - **Missing data**: the sandbox didn't send everything the check needs.
3. If there is **no row at all**, the sandbox could not reach Segue. Check that the API is running and that the
   browser allowed the call.

## Connecting a real EHR

1. Ask your administrator to set `Cds:PublicBaseUrl` to the address the EHR will use to reach Segue.
2. Add and turn on the services you want.
3. On **Connect your EHR**, copy the **Discovery URL** and send it to your EHR team. Ask them for the **issuer
   (iss)** and **JWKS URL** (or the public keys) their EHR signs with.
4. Click **Add issuer** and enter those values. Leave **Audience (aud) override** blank unless the EHR team says
   their EHR sends a different value.
5. Ask the EHR team to turn on the hooks in the EHR.
6. Watch **Activity** for the first calls.

EHR support: **Epic** and **athenahealth** support CDS Hooks 2.0. Support in **eClinicalWorks** is not confirmed;
check with eCW first. Epic needs a few extra steps; see the next section.

## Connecting Epic

Epic does **not** read the discovery URL. The health system's Epic team types each service into Epic by hand.
Segue gives them everything on one page: the **Epic setup sheet**.

Before you start, ask your administrator to set `Cds:PublicBaseUrl` to the address Epic will use to reach Segue,
and ask the Epic team for:

- the **issuer (iss)**: their FHIR address, for example `https://ehr.example.org/api/FHIR/R4`;
- the **JWKS URL**: the address of their public keys (the one their calls name as `jku`);
- the **client id** they registered for Segue.

### Step 1: add Epic as a trusted issuer

1. Click **Clinical Decision Support** in the left-hand menu.
2. Click the **Connect your EHR** tab.
3. Click **Add issuer**.
4. In **Name**, type a name, for example `Epic production`.
5. In **EHR vendor**, type or pick `Epic`.
6. In **Issuer (iss)**, paste the issuer.
7. In **JWKS URL**, paste the JWKS URL. Don't paste keys instead: Epic names its key address in every call, and
   Segue refuses a call whose key address doesn't match this JWKS URL.
8. Leave **Audience (aud) override** blank.
9. In **Client id (sub)**, paste the client id.
10. Leave **EHR FHIR server** blank. Segue then uses the issuer address, which for Epic is the FHIR address.
11. Keep **Let Segue fetch missing data from this EHR** ticked.
12. Click **Add issuer**.

### Step 2: check Segue can reach Epic's FHIR server

1. In the **Trusted issuers** list, click **⋮** on the Epic row.
2. Click **Test callback**.
3. Wait for the result. A tick means the server answered; the window also shows its FHIR version and software.
   Segue sent no patient data and no access token.
4. Click **Close**.

If it fails, check the issuer address with the Epic team, and that Segue's server can reach it.

### Step 3: hand the Epic setup sheet to Epic

1. Still on **Connect your EHR**, scroll to **Epic setup sheet**.
2. Click the **Service** box.
3. Click the service you want to set up in Epic.
4. Read any yellow warnings at the top and fix them first (for example "no expected sub" or "public base URL not
   set").
5. Click **Download JSON** (or the copy button) and send the file to the Epic team. They can also read the tables
   on screen:
   - **1. The service**: the service endpoint, the feedback endpoint, and the token audience (aud).
   - **2. Who signs the calls**: the issuer, key set (jku), client id (sub), audience and FHIR server Segue expects.
   - **3. Prefetch to configure**: each prefetch key and its FHIR query. **Segue fetches it** means Segue reads it
     itself if Epic leaves it out.
   - **4. Dismiss reasons to map**: each code to map to one of their Epic override reasons.
   - **5. One-click fixes**: the preference list items or codes the fixes use, if fixes are on.
   - **6. Card links**: the SMART apps they must add to their SMART app allow-list.
6. Repeat from 2 for each service.

The sheet contains settings only, no patient data. You can also open it from a service's setup page: click
**Epic setup sheet** next to **Data the EHR sends**.

### Step 4: watch the first calls

1. Click the **Activity** tab.
2. Each Epic call shows labels such as **Epic trigger 18** and **Epic 11.2**.
3. **Not signed in** with "jku is not this issuer's configured JWKS URL": the JWKS URL you entered is not the one
   Epic uses. Edit the issuer.
4. **Not signed in** with "sub is not this issuer's expected client id": the client id is wrong. Edit the issuer.

## Turn on one-click fixes safely

A fix changes the clinician's drafts in the EHR when clicked. Turn fixes on only after your clinical team has
checked every code, and test them on the **Test** tab first.

Which fixes each check offers:

| Check | Fixes |
|---|---|
| Drug–allergy check | Remove the draft order; Add an alternative medication; Add to the problem list; Add an encounter diagnosis |
| Drug–drug interaction check | Remove the draft order (either drug, if both are drafts); Add an alternative medication; problem; encounter diagnosis |
| Renal dose check | Remove the draft order; Add an alternative medication; problem; encounter diagnosis |
| Care-gap reminders | Add the test or procedure order; problem; encounter diagnosis |
| Prior-authorization reminder | Add to the problem list; Add an encounter diagnosis |

**Add an encounter diagnosis** appears only when the clinician is in an encounter.

1. Click the **Services** tab.
2. Click **⋮** on the service, then **Edit**.
3. Scroll to **One-click fixes**.
4. Tick **Offer one-click fixes on this service's cards**.
5. Tick each fix you want, for example **Remove the draft order**.
6. For a fix that adds something, add its codes (next section). A fix with no code is never offered.
7. Optional: in **Pre-select a fix**, pick the fix the EHR should tick in advance. The clinician can still untick it.
8. Click **Save changes**.
9. A window asks **Let this service change EHR drafts?** Click **Save**.
10. Back on **Services**, the service shows **Changes EHR drafts**.

To turn fixes off, untick **Offer one-click fixes on this service's cards** and click **Save changes**.

### Map the codes a fix uses

Each fix that adds something has a code table. Each row says what to add, and when.

1. Under the fix, click **Add a code**.
2. In **Applies to**, pick the case this row is for (for example a drug class, a medicine or a care gap), or leave
   **Any** to use the row for every case. A row for the exact case wins over the **Any** row.
3. In **Code system**, pick one:
   - medicines: **Epic preference list item**, **RxNorm** or **NDC**;
   - tests and procedures: **Epic preference list item** or **Epic order set item**;
   - problems and diagnoses: **ICD-10-CM**, **Epic CMS-HCC** or **Epic HHS-HCC**.
4. In the code column, type the code. For Epic preference list items, get the item id from the Epic team.
5. In the name column, type the name the clinician sees (required for medicines, problems and diagnoses).
6. Repeat for each case. You can't have two rows for the same case.
7. Click **Save changes**.

Epic uses its own defaults (dose, frequency and so on) for an order a fix adds; it uses only the code.

### See a fix before turning it on for real

1. Click the **Test** tab.
2. Click the **Service** box, then the service.
3. Click **Load sample request**.
4. Click **Run**.
5. Under a card, click a fix button to tick it.
6. Read the preview: "The EHR would remove the draft ..." or "The EHR would add a draft ...". Click **Resource sent
   to the EHR** to see the exact data.

Nothing is sent to any EHR from the Test screen.

## Change the dismiss reasons

Cards offer four standard reasons until you change them. The Epic team maps each code to one of their own override
reasons, so agree on codes with them and don't change a code once Epic uses it.

1. Open the service (**Services** → **⋮** → **Edit**).
2. Scroll to **Dismiss reasons**.
3. Click **Change the reasons**. The standard reasons are copied in for you to edit.
4. In **Code**, use 1 to 50 letters, digits, hyphens or underscores, with no spaces.
5. In **Shown to the clinician**, type the text.
6. Click **Add a reason** for more (up to 10), or **×** to remove one (at least one must stay).
7. Click **Save changes**.

**Go back to the standard reasons** undoes your list.

## Add card links

1. Open the service (**Services** → **⋮** → **Edit**).
2. Scroll to **Card links**.
3. Click **Add a link**.
4. In **Button text**, type the label, for example `Dosing guide`.
5. In **Opens**, pick **A web page** or **A SMART app in the EHR**.
6. In **Web address** (or **App launch URL**), paste an address starting with `https://`.
7. For a SMART app only: optionally fill in **App context** (no patient details), and tick **Let the EHR open the
   app on its own, without a click** if wanted.
8. Click **Save changes**.

A SMART app must be on the health system's SMART app allow-list, or the EHR won't open it. The Epic setup sheet
lists it for them.

## Good to know

- **No patient data is kept.** Not in Activity, not in logs, not from the Test screen. Data Segue reads from the
  EHR is used for that one call and forgotten; the EHR's access token is never stored.
- **Sample rules.** Have your clinical team review each check's rules and settings before real use; for prior
  authorization, check the code list against your payers' rules. The same goes for every fix code.
- **Fixes change drafts only.** With fixes off (the default), cards only advise. With fixes on, a click can remove
  a draft order or add a draft order, problem or diagnosis; the clinician still signs. Segue never removes a signed
  order.
- **Speed.** If a check takes longer than about 1.5 seconds, Segue answers with no cards rather than slow the
  clinician down. Reading missing data from the EHR gets about 0.9 seconds of that. Activity shows it as
  **Missing data**.
- **Feedback.** Some EHRs report whether the clinician accepted or overrode a card. Activity lists these as
  **Feedback** with the outcome **Feedback received**: counts, fixes accepted and reason codes only; comments are
  never stored.
- **Deleting.** A trusted issuer can't be deleted while a service lists it under **Who can call it**. Untick it
  there first.
