# Microsoft Fabric as a destination — surface by surface

Fabric is not one destination. Each item type below is a different protocol, a different auth audience and a
different failure mode, which is why they are separate landing strategies behind `IFabricLandingStrategy` rather
than options on one writer.

This document records what is built, what is verified against a live tenant, and what each unverified piece needs
before it can be enabled.

## Status

| Fabric item | Status | What it needs |
|---|---|---|
| OneLake / Lakehouse Files | Shipping | — verified end to end |
| Warehouse | Shipping | — verified end to end |
| Lakehouse Tables (Delta) | Built, gated off | A live tenant write |
| SQL Database in Fabric | Works via the SQL Server destination | A live tenant test |
| Cosmos DB in Fabric | **Not supported** | A new writer (NoSQL API) |
| Mirrored Database | Not applicable | Read-only by design — write to the source |
| Eventstream | Served elsewhere | Use the Data Lake Webhook destination |
| Eventhouse / KQL | Not built | A Kusto ingest writer |

## Lakehouse Tables (Delta)

Built and unit-tested, but **not enabled** — `enabledFabricModes` in both phase-config services deliberately omits
`lakehouseTable`. A Delta log a reader rejects fails unhelpfully: the write reports success and the table simply
never appears, so this stays gated until a live write has been confirmed, exactly as Warehouse was.

### Why the log is hand-written

A Fabric Lakehouse registers a `Tables/` folder as a table only when a `_delta_log` is present. `OneLakeFiles`
writes the same Parquet without one, which is why it refuses a `Tables/` path outright.

The .NET Delta bindings wrap the Rust kernel through native interop, which adds a per-platform native asset to a
deployment that is currently pure managed code, and their licence position needed review. Against that, what an
append-only writer needs from the protocol is small and fully specified: a `protocol` action, a `metaData` action,
and one `add` action per file, as NDJSON in a zero-padded commit file. That subset is implemented in
`DeltaTransactionLog`, following the [Delta Transaction Log Protocol](https://github.com/delta-io/delta/blob/master/PROTOCOL.md).

### Append-only, deliberately

The writer emits `add` actions and never `remove`, so it cannot express an update or a delete. Upsert into Delta
means rewriting the data files holding matched rows and committing add+remove together, which needs read-side
Parquet and conflict resolution against concurrent writers — materially larger, and not attempted.

A destination configured for Upsert is **refused at write time** with a message pointing at the Warehouse surface,
rather than silently appending and leaving duplicates the customer would find later in their own reporting.

### Concurrency

Delta's commit protocol relies on the store refusing to overwrite an existing commit file. The commit upload is
conditional (`If-None-Match: *`), so two writers racing for version N produce one winner and one `409`, which
retries at the next version. An unconditional write would let the loser destroy the winner's commit.

Data file first, commit second: a data file with no commit referencing it is invisible to readers — wasted space,
not corruption — whereas a commit naming a file that does not exist breaks the table for everyone.

### What to check first if a table does not register

1. The table folder is directly under `Tables/`. Fabric does not discover a table nested in a subfolder unless the
   Lakehouse is schema-enabled, in which case it is `Tables/{schema}/{table}` — set `dest_fabricLakehouseSchema`.
2. The commit file is exactly `_delta_log/00000000000000000000.json`, zero-padded to 20 digits.
3. `schemaString` is a JSON **string**, not a nested object. This is the easiest field in the protocol to get
   wrong, and it is asserted by a test for that reason.

### How to test it

1. Add `'lakehouseTable'` to `enabledFabricModes` in **both** `phase-config.service.ts` and
   `phase-config-v2.service.ts`.
2. Create a Fabric destination, choose **Lakehouse table (Delta)**, and give it the workspace and lakehouse.
   Use **GUIDs, not display names** — see the friendly-name note below.
3. Run a workflow. Expect a `Tables/{name}` folder containing one `part-*.parquet` and a `_delta_log`.
4. Open the Lakehouse in Fabric. The table should appear under Tables and be queryable from the SQL analytics
   endpoint.

## SQL Database in Fabric

**No new writer needed** — the existing SQL Server destination reaches it. But the reason is not the one originally
assumed, and the correction is worth recording.

A SQL Database in Fabric has no SQL logins at all; every documented connection method authenticates through
Microsoft Entra. The initial assumption was that the generic SQL path therefore could not reach one, because
`SqlServerConnectionFactory` did `new SqlConnection(connectionString)` with no token handling.

That was wrong. **SqlClient implements Entra natively** via the connection string's `Authentication` keyword —
`Active Directory Default` (3.0+) runs the full `DefaultAzureCredential` chain, and `Active Directory Service
Principal` (2.0+) reads User Id and Password as client id and secret. This solution is on SqlClient 5.2.2, and
Azure.Identity already arrives transitively with it.

An intermediate version of `SqlServerConnectionFactory` intercepted those modes to attach a token by hand. That was
removed: it re-implemented what the driver already does, adding a second credential chain to keep in step and
needing a tenant id the connection string has no field for, without adding capability.

What remains is a **guard**, not a translation. It refuses two connection strings that cannot work as written:

- `Active Directory Service Principal` missing User Id or Password.
- A `Password` left on a mode that ignores it — the fingerprint of a SQL-auth connection string with an
  `Authentication` keyword bolted on.

Both otherwise surface as an authentication failure, which reads like a missing role assignment and sends someone
to check permissions that are already correct.

Note the contrast with `FabricWarehouseConnectionFactory`, which genuinely does attach a token: a Warehouse
destination configures its identity through the wizard's own auth fields, so there is no `Authentication` keyword
for the driver to act on.

### How to test it

Create a SQL Server destination whose connection string is the one Fabric shows under **Settings → Connection
strings**, with an Entra mode added:

```
Server=<id>.database.fabric.microsoft.com,1433;Database=<db>;Authentication=Active Directory Default;Encrypt=True
```

For a service principal, use `Authentication=Active Directory Service Principal` with the app id in `User Id` and
the secret in `Password`. The identity needs access granted inside the database, not only in Fabric.

The target table must already exist — FHIRBridge never issues DDL against a customer's destination schema (see
[11-destination-schema-ownership-plan.md](11-destination-schema-ownership-plan.md)).

## Cosmos DB in Fabric — not supported

This row was previously assumed to work through the existing Mongo writer. **It does not.**

Cosmos DB in Fabric is the **NoSQL API** — Microsoft's own documentation states it "uses the same engine, same
infrastructure as Azure Cosmos DB for NoSQL". It does not expose the MongoDB wire protocol, so `MongoDB.Driver`
cannot connect to it. (Azure Cosmos DB *for MongoDB* is a different product, and Fabric's Data Factory connector
for it addresses external accounts, not the Fabric-native item.)

Supporting it means a new writer against `Microsoft.Azure.Cosmos`, with Entra auth. Not built.

## Mirrored Database — not applicable

Read-only by design: a managed replica of an external source. Write to the source database it mirrors.
`FabricDestinationSettings.Parse` refuses the item type by name and says so.

## Eventstream — served elsewhere

An Eventstream custom endpoint is plain authenticated HTTP, already served by the **Data Lake Webhook**
destination. `FabricDestinationSettings.Parse` refuses the mode and redirects by name. A second, thinner
implementation of the same wire protocol would be worse than the redirect.

## Eventhouse / KQL — not built

The only genuine remaining gap. Different protocol (Kusto ingest), different SDK
(`Microsoft.Azure.Kusto.Ingest`, first-party Microsoft, so no licence question), different batching semantics.
No reuse from the OneLake or Warehouse work.

## Cross-cutting notes

### Friendly names vs GUIDs

Some tenants disable OneLake friendly-name support. The **blob** endpoint tolerates display names while the **DFS**
endpoint does not, so a file upload can succeed by name moments before a COPY INTO reading the same path fails with
what Fabric reports as an "unsupported URL" — which looks like a malformed path rather than a naming-mode problem.

**Address workspaces and items by GUID.** Both are in the Fabric URL when the item is open.

### The two metadata allowlists

A `dest_*` key absent from **both** of these is silently dropped before save:

- `portal/src/app/destination-connections/utils/destination-connection-secret.util.ts`
- `portal/src/app/services/workflow-build-assembler-v2.service.ts`

This has caused two separate bugs. Any new Fabric field must be added to both.

### Schema ownership

FHIRBridge never issues DDL against a customer's destination schema. The Warehouse strategy verifies the target
table exists and throws when it does not.

Delta is the one exception, and for a specific reason: writing version 0 **is** creating the table, since the
protocol requires the first commit to carry a `metaData` action. There is no pre-existing customer object to take
ownership of — the table is the folder the destination is configured to write, and its schema is exactly the
mapping. The Warehouse case is different because the table is a customer-owned object in a database FHIRBridge
does not own.
