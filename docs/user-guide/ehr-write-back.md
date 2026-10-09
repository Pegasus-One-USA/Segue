# Writing data back into an EHR — admin guide

This guide shows how to set up a workflow that reads records from one place and writes them into an EHR (Epic,
eClinicalWorks or athenahealth), or into a FHIR test server to try it first. It also covers sending EHR data to a database such as SQL
Server or MongoDB.

Technical background: [21 — Source / destination consistency](../backend/21-source-destination-consistency.md) and
[20 — EHR write-back](../backend/20-epic-r4-write-back.md).

## The idea in one minute

- A workflow has a **source** (where records come from) and one or more **destinations** (where records go).
- A **source only reads.** It can be Epic, athenahealth, eClinicalWorks, a SQL database or CSV files.
- A **destination writes.** It can be an EHR, a FHIR server, a database, files and more.
- The **same EHR can be both**: as a source it is read, as a destination it is written. It uses a different
  connection for each.
- The **source decides which kinds of records (resource types) are read**, for example Patient, AllergyIntolerance
  and Condition. Each destination writes some or all of those types, never more.
- Connections are kept in two lists: **Source Connections** for reading and **Destination Connections** for writing.

## Words used in this guide

| Word | Meaning |
|---|---|
| **EHR** | Electronic health record system, such as Epic, eClinicalWorks or athenahealth. |
| **FHIR** (also "FHIR R4") | The standard format EHRs use to exchange health records. R4 is its current version. |
| **FHIR server** | A system that stores records in FHIR format. A **test server** is a FHIR server set up only for trying things out, such as the one your IT team runs in the local test setup. It is not an EHR and holds no real patients. |
| **FHIR base URL** | The web address of a FHIR server, given to you by whoever runs it. |
| **Resource type** | A kind of record, for example Patient, AllergyIntolerance (allergies) or Condition (problems). |
| **Identifier** | A number that identifies a patient, such as a medical record number. |
| **Matching** | Finding the right patient in the EHR. Segue looks the patient up by identifier and may ask the EHR to compare name, birth date and other details. It writes only when the EHR names one patient for certain. |
| **Backend System** | The kind of EHR app login Segue uses when it works on its own, with no person signed in to the EHR. |
| **Audience** | Who an EHR app login is for (for example Backend System). Shown in the Source Connections list. |
| **Vendor write APIs** | The writing features eClinicalWorks or athenahealth must switch on for your practice before Segue can really write there. |
| **Data set identity** | A key Segue makes for a SQL database or CSV file source (under **Advanced**). Segue uses it to remember which rows it has already written. You rarely need to change it. |
| **Partial success** | The run finished, but some records were skipped; the report says which and why. |
| **Ready** | A workflow's status once it is saved and complete enough to run. |

## 1. The connection lists

Both lists are under **Settings → Workflow Configurations**.

### Source Connections

This list holds every connection a workflow reads from: EHR connections and SQL databases.

1. Open **Settings**, then **Workflow Configurations**, then **Source Connections**.
2. The **Type** column says what each row is, for example "Epic" or "SQL database (SQL Server)".
3. Use the filters at the top to narrow the list: **All connections** / **EHR** / **Database**, **All EHRs**,
   **All audiences**, **All statuses**. Type in the search box to find a row by name, type or base URL.
   **Reset** clears the search and filters.
4. To add a connection, click **New**. A window "New source connection" opens with an **EHR** group and a
   **Database** group. Click the card you need:
   - an EHR card (for example **Epic**) opens that EHR's connection form. Fill it in and click **Save**;
   - **SQL database** opens "New database connection". Enter a **Name**, choose the **Engine** (SQL Server / Azure
     SQL, PostgreSQL or MySQL), paste the **Connection string**, and click **Save**. Use a database login that can
     only read.
5. Each row has a **⋮** menu (More actions):
   - EHR rows: **View**, **Edit**, **Delete**. **Edit** is greyed out ("used by a workflow") while a workflow reads
     through the connection. **Delete** is greyed out while a workflow reads or writes through it.
   - Database rows: **Test**, **Edit**, **Delete**. **Test** checks that Segue can log in to the database, that the
     login can only read, and that a simple query runs. Only database rows have Test for now. Database rows are
     never greyed out.
   - The greyed-out locks are shown only to administrators (Global Admin or Tenant Admin). Other roles see Edit and
     Delete enabled, so check that no workflow uses a connection before deleting it.

### Destination Connections

This list holds every destination and every **EHR write connection** (the login Segue uses to write into an EHR).

1. Open **Settings → Workflow Configurations → Destination Connections**.
2. The **Type** column says what each row is: a destination such as "SQL Server" or "EHR write-back — Epic", or a
   login such as "EHR write connection — Epic". The **Vendor write APIs** column shows "Activated" or "Not
   activated" for eClinicalWorks and athenahealth.
3. Filter by type (**All types**, **All destinations**, **All EHR write connections**, or one type) and by status.
4. To add one, click **New**. In "New destination connection", the **EHR** group has up to four cards (you see
   only the ones your role may create; they need the EHR Write-Back permission):
   - **Epic**, **eClinicalWorks**, **athenahealth**: opens the EHR's form, already set up for writing (Backend
     System). For eClinicalWorks and athenahealth, tick **Vendor write APIs activated** only once the practice has
     turned those APIs on; until then every write to this connection only checks and sends nothing. Ticking it
     needs the EHR Write-Back edit permission. For athenahealth, fill in **Department ID**: the department new
     patients are registered in.
   - **FHIR test server**: a small form with **Name** and **FHIR base URL**. A test server receives exactly what
     an EHR would; nothing reaches the EHR.
5. Click **Save**.
6. **Edit** is always available on an EHR write connection, so you can tick **Vendor write APIs activated** later.
   **Delete** is greyed out ("used by a workflow") while a workflow writes through it, or, for a "Read & Write"
   connection, while a workflow reads through it. As on Source Connections, only administrators see this lock.

> An older connection that can both read and write ("Read & Write") appears on both lists. That is expected.

## 2. Start a workflow and add a source

1. In the side menu click **Workflows**, then **New**, give the workflow a name and open it.
2. Click **＋ Add module**. The node library opens.
3. Click the source you want. Its form opens.

### 2a. A SQL database or CSV files as the source

1. Click **SQL database** or **CSV file**.
2. Choose where the rows come from:
   - **SQL database**: under **Read from database**, pick a saved database. Click **Test** to check that Segue can
     log in, that the login can only read, and that a simple query runs, or **New database** to add one here (it is
     then also listed under Source Connections);
   - **CSV file**: under **CSV files**, upload one file per resource type, or one file with a column that says
     which type each row is. Up to 10 MB and 50,000 rows each; the first line must be the column names.
3. The **Name** fills in by itself ("SQL: <database>" or "CSV: <first file>"). You can change it, but you do not
   have to. Segue also makes the **Data set identity** (under **Advanced**): it is how Segue knows a row was already
   written, so a corrected file is not written twice. For a database it comes from that saved database; for CSV files
   it is made once, when you add the source. A saved identity never changes by itself. Change it only when you
   replace a source with a new one that reads the same data.
4. Under **Resource types to read**, tick each type this source reads. For writing into an EHR, include
   **Patient**, and give every record an id column.
5. A card appears for each ticked type. In each card:
   - database: type the **Query**: one SQL statement that reads data (it starts with SELECT, or WITH). It may combine
     tables (joins) and use views. Ask whoever looks after the database if you need help;
   - CSV: choose the **File**. If one file holds several types, use **Only rows where (optional)**: choose the
     column, then type the value that marks this type in the box ("equals…");
   - **Template**: click it to open. It shows the shape of one record in FHIR format, with `{{column}}` marking
     where each value from your query or file goes (for example `{{birth_date}}`). A starting template is filled in
     for you; **Use the starting template** puts it back.
6. Click **Check**. **Without reading any rows**, Segue checks every card:
   - database: it asks the database whether every table, view and column exists, and whether each query returns
     the columns its template needs;
   - CSV: it compares the columns the template needs with the file's first line (its column names).

   Each card then shows **Passed** or **Needs fixing**, with the reason (for a database, in the database's own
   words). **Check and preview first rows** also shows the first records built.
7. When the line under the buttons reads **Every resource type passed.**, click **Add to Workflow** (or **Save**
   when editing). Saving is only possible after a passed check.

### 2b. An EHR as the source

1. Click the EHR tile, for example **Epic**.
2. Under **Read from: saved Epic connection**, pick a connection from Source Connections, or fill in the form to
   create a new one.
3. Scroll to **Resource types to read** and tick the types to read. Use the search box, **Select all** or
   **Clear**. Destinations can only write the types ticked here.
4. Click **Add to Workflow**.

> A source saved before this screen existed shows a note under Resource types to read. It keeps working as before;
> changing the list makes the source declare its types like a new one.

## 3. Add an EHR destination

1. On the source node, click **＋** (Add next module).
2. In the node library, click the **EHR** heading. It opens to show three tiles: **Epic**, **eClinicalWorks** and
   **athenahealth**. Click the one to write to. The heading and tiles appear only if your role
   has an EHR Write-Back permission; if you do not see them, ask an administrator.
3. The window shows four steps: **Connection → Resource types → Options → Review**. Use **Next** and **Back** at
   the bottom.

### Step 1 — Connection ("Write to …")

1. Under **Connection**, choose where to write. The list has two groups:
   - **Epic** (or eClinicalWorks / athenahealth): the EHR's own write connections. Choosing one is a **live** run:
     records are written into the EHR, and EHR writes cannot be undone;
   - **Test servers (receive exactly what Epic would)**: FHIR test servers. Choosing one is a **test run**: the
     records are really written, but to the test server, shaped exactly as the EHR would receive them. Nothing
     reaches the EHR. The line under the list reads "Test run: nothing reaches Epic."

   Nothing is chosen for you, so a live run is always your own choice. To make a connection here, click **New Epic
   connection** or **New test server**. A new test server is chosen for you; a new EHR connection is not ("The new
   connection was added. Choose it above to write into Epic.").
2. Click **Next**. The destination takes the connection's name; you can change it on Review.

### Step 2 — Resource types

1. The types your source reads are listed. On a **new** destination, the types this EHR writes without an extra
   choice are already ticked, and the line at the top reads "These are the N resource types your source
   reads. Untick any this destination doesn't need." (This happens only when the source lists its types under
   **Resource types to read**; a reopened destination keeps what was saved.)
2. Untick what this destination should not write.
3. **Greyed types** are read by the source but cannot be written here. The card says why:
   - "Not accepted by Epic" — this EHR has no way to receive that type;
   - "Needs a CSV / SQL Table source" — it needs the EHR's own ids, which only your own data can supply;
   - "Not read by this destination's source" — it was ticked before, but the source no longer reads it. Untick it.
4. A type the EHR writes in more than one way lists its **kinds of record** under it, for example Observation →
   Vital signs, Lines, drains and airways; DocumentReference → Clinical notes, Scanned documents; Condition
   (eClinicalWorks) → Problem list, Encounter diagnosis, Medical history.
   - A kind marked "always included" is ticked whenever the type is; Segue picks it from each record's shape. While
     the type is unticked it reads "included whenever <type> is ticked".
   - The other kinds start unticked; tick the ones this destination should write. Ticking a kind ticks its type, and
     unticking a type's last kind unticks the type. A type written only through such kinds (for example Epic
     BodyStructure → Radiotherapy volumes) ticks all its kinds when you tick the type.
   - Kinds that need the EHR's own ids are shown only when the source is a CSV / SQL Table source.
   - eClinicalWorks Medical history and Surgical history are filed on a new telephone encounter. Ticking either turns
     that on; the note under it says so, and there is nothing to set later. One setting serves both, and the note under
     each says what clicking it does to the other: "Also ticks …", "Unticking also unticks …" (naming the type too
     when that was its only kind), or "Same setting as …".
   - A destination saved before kinds were chosen here may show "Tick at least one, or untick the type." under a
     type; **Next** waits until you do.
5. Some tickable types carry a note:
   - "Sent as a dry run until Vendor write APIs activated is ticked on the connection";
   - "Sent as a dry run only for now" — Segue cannot yet write this type for real to this EHR, whatever the
     connection says;
   - "Created only when "Create the patient when the EHR has no match" is on under Options".
6. Click **Next**.

### Step 3 — Options ("how records are written to …")

- **Max writes per run** (default 500).
- **Clinical notes are filed as**: **Preliminary (a clinician reviews and signs)** or **Final (signed under the
  integration user)**.
- **Create the patient when the EHR has no match**: off by default. Patients are matched, never guessed; an
  uncertain match is left for review. It only helps when the EHR clearly answers that it has no such patient (see
  Troubleshooting).
- eClinicalWorks: **Note author (eCW practitioner id)**.
- athenahealth: **Provider id (athena)** and **Department id (athena)**. Leave the department empty to use the
  connection's department. If neither this box nor the connection has a department, new patients are rejected.
- **Dry run: check every record, send nothing**: shown only when an admin has turned Dry run on under **Settings →
  System Settings** (`EhrWriteBack:DryRunEnabled`, off by default). Segue checks each record against the real EHR —
  finds the patient, looks for a visit where needed, checks what Segue has already sent — and writes nothing. Only a
  dry run checks against the **real** EHR; a test server cannot tell you whether the EHR will find your patients. A
  destination saved as a dry run stays one even if the setting is later turned off, and shows "Dry run is turned off
  in System Settings. This destination stays a dry run until you untick it."

Click **Next**.

### Step 4 — Review

1. **Destination name**: left empty, the destination takes the connection's name (shown in grey). Type to change it.
2. Check the cards: **Destination type** (for example "EHR write-back — Epic"), **De-identification**, **Writes to**
   (connection and EHR), **Mode** (for example "Live: writes into Epic" or "Test run: nothing reaches Epic"),
   **Patients** and **Resource types** (each with the kinds of record written, for example "Observation: Vital signs,
   Lines, drains and airways").
3. For a live run, a red line reads "Live: records will be written into Epic. This cannot be undone."
4. Click **✓ Add to Workflow**, then click **Save** at the top of the workflow builder.

## 4. A database destination (SQL Server, MongoDB and others)

1. On the source node click **＋**, then the destination, for example **SQL Server** or **MongoDB**.
2. Steps: **Connection → Resource types → Map fields → Review**.
3. **Connection**: choose a saved connection, or click **New connection** and fill in the form, then **Test
   connection & Next** (SQL) or **Next**. There is no name to type here: the destination takes the connection's
   name, and you can change it on **Review** (**Destination name**).
4. **Resource types**: on a new destination whose source lists its types, the source's types start ticked. Untick the ones this destination does not need. One source
   can feed several destinations that each take a different part, for example three types to SQL Server and two
   to MongoDB.
5. **Map fields**: click **Map** beside each type and connect the record fields to your columns.
6. **Review**, then **✓ Add to Workflow**, then **Save** the workflow.

## 5. Run the workflow and read the report

1. Go to **Workflows**. In the row's **⋮** menu click **Run** (the workflow must be Ready). If the workflow writes
   live into an EHR, Segue first asks "Write into Epic now?" (records written cannot be undone). Click **Run** to go
   ahead. It asks until a run of the workflow succeeds; after you edit and save the workflow, it asks again.
2. When it has finished, open **Execution History** and click the run.
3. On the write-back step click **View dry-run report** (after a dry run) or **View write-back report** (after a
   live run, and also after a test run).
4. The report title and badge say which kind of run it was:
   - **Dry-run report** / **Dry run**: nothing was sent. **Would write** is what a live run would write;
   - **Test-run report** / **Test run**: written to the FHIR test server, not to the EHR;
   - **Write-back report** / **Live**: written into the EHR.
5. The columns per resource type: **Would write**, **Written**, **Already written**, **Skipped**, **Rejected**,
   **Unknown**.
   - **Already written** means an earlier run wrote it; it is never sent twice.
   - Under each type, the reasons are listed with a count, for example "Patient could not be found in the EHR",
     "No suitable visit for this patient in the EHR", "Not active", "Entered in error".
   - **Unknown** means it was sent but no answer came back. It is not resent automatically; check it in the EHR.
6. **Copy report** copies the report text. Reports hold counts and reasons only, no patient details.

**Other FHIR destinations.** On the **Aidbox** and **Azure FHIR Service** destinations (not an EHR write-back) with the write mode **Upsert by resource id (PUT)**, a record the server refuses is now skipped and
reported with the reason; the rest of the run carries on and finishes as a partial success.

## 6. Troubleshooting

| You see | Why | What to do |
|---|---|---|
| "Patient could not be found in the EHR", "Patient not found by identifier", "Uncertain patient match, left for review" | Segue only writes to a patient it can find for certain. | Make sure the source sends identifiers the EHR knows (and name, birth date, gender, phone, address for matching). If the EHR clearly answers that it has no such patient, ticking **Create the patient when the EHR has no match** under Options lets Segue create one. |
| An older destination that writes plain FHIR to a FHIR server (made with the former **FHIR server** tile; it still runs) writes nothing for new patients ("Patient match failed" or "Uncertain patient match, left for review") | Many plain FHIR servers (for example a local test server) cannot compare patient details, so they answer with an error instead of "no such patient". Segue never guesses, and **Create the patient when the EHR has no match** does not help here: it is used only after a clear "no such patient". | Send patients with an identifier the server already has. (A **Test** run treats "not found" as "no such patient", so there the create option does work.) |
| A type is **greyed** on Resource types | The EHR does not accept it, or it needs your own data source, or the source no longer reads it. The card gives the reason. | Follow the reason, or leave the type out. |
| **Next** stays unavailable on Options | A ticked type needs an option turned on (its note on Resource types says which). | Turn that option on, or go **Back** and untick the type. |
| A type you want is **not listed at all** | The source does not read it. | Open the source and tick it under **Resource types to read**. |
| Save is refused: "Destination '…' writes …, which its source '…' does not read." | A destination lists a type its source no longer reads. | Add the type to the source's resource types, or remove it from the destination. |
| CSV / SQL card says **Needs fixing** | A table, view or column is missing, the query returns too few columns for the template, or (CSV) the file has no column the template needs. | Fix the query, file or template as the message says, then click **Check** again. |
| Everything runs as a dry run on eClinicalWorks or athenahealth | **Vendor write APIs activated** is off on the write connection. | Turn it on (Destination Connections → Edit) once the practice has those APIs. You need the EHR Write-Back edit permission. Types noted "Sent as a dry run only for now" stay dry runs even then. |
| **Delete** (or **Edit** on Source Connections) is greyed on a connection | A workflow still reads or writes through it. Only administrators see this lock. | Change or remove that workflow first. |
| No **Test** on an EHR or destination row | Only database rows can be tested for now. | Run the workflow against a test server, or as a **Dry run** when your admin has turned it on, to check an EHR connection. |
