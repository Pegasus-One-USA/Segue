# Releasing Segue

How a customer-facing version gets made. For what customers do with the result, see [UPGRADE.md](UPGRADE.md).

## The model

```
main_v2  ──●────●────●────●────●──────   QA. All feature work lands here first.
            \              \
release  ────●──────────────●─────────   What customers get. Never developed on directly.
             │              │
           v1.0.0        v1.1.0          ← tags. These ARE the versions.
```

- **`main_v2`** — QA. Can be briefly broken; that is what QA is for.
- **`release`** — only ever receives a merge from `main_v2` when cutting a version, or a
  cherry-picked fix for a patch. Always shippable.
- **Tags** — cut on `release`, never on `main_v2`. A tag is permanent and immutable: it is the only
  thing that can identify what a customer actually has six months from now. A branch moves, so it
  cannot.

Api, Worker and Portal are versioned as **one product**. They ship from one deploy template, share
one database schema, and a customer cannot mix versions of them — independent numbers would buy
nothing and cost a compatibility matrix.

## Where the version lives

One file: **`VERSION`** at the repo root. Everything derives from it.

| Artifact | How it gets the version |
|---|---|
| .NET assemblies | `Directory.Build.props` reads `VERSION` into `VersionPrefix` |
| Angular portal | Dockerfiles write `portal/src/app/version.ts` from the `APP_VERSION` build-arg |
| Container images | `build-images.ps1\|sh` default `-Tag` to `VERSION` |
| ARM template | `release.yml` rewrites `imageTag` in `main.bicep` to the released version |

Never hand-edit a version into a `.csproj`, `package.json`, or component. Change `VERSION`.

## Choosing the number

Semantic versioning, and it is a **judgment call** — deliberately not derived from commit messages,
because automation gets the MAJOR bump wrong exactly when it matters most.

| Bump | When |
|---|---|
| **PATCH** (1.0.0 → 1.0.1) | Fixes only. No schema change. |
| **MINOR** (1.0.0 → 1.1.0) | New features, additive migrations. Nothing breaks. |
| **MAJOR** (1.0.0 → 2.0.0) | Customer must do something manual to upgrade: config keys removed or renamed, a non-reversible migration, a minimum-version hop. |

## Cutting a release

```bash
git checkout release
git merge main_v2

# Update VERSION to the number you are releasing, then commit.
echo "1.1.0" > VERSION
git commit -am "Release 1.1.0"

git tag v1.1.0
git push origin release v1.1.0
```

Pushing the **tag** is what fires [`release.yml`](../.github/workflows/release.yml). It:

1. Derives the version from the tag name (`v1.1.0` → `1.1.0`).
2. **Fails if `VERSION` disagrees with the tag** — a mismatch would stamp assemblies with one number
   and images with another.
3. Builds and runs the full test suite.
4. Builds and pushes all five images tagged `1.1.0`.
5. Rewrites `imageTag` in `main.bicep` to `1.1.0` and compiles `main.json`.
6. Creates a GitHub Release with the deploy artifacts attached.

`workflow_dispatch` runs the same build as a dry run and never pushes.

## Hotfixing a released version

The reason `release` exists as a branch and not just a set of tags. A customer is on 1.1.0;
`main_v2` is half-way through 1.2 work; you cannot ship them 1.2-in-progress.

```bash
git checkout release          # still looks like 1.1.0
git cherry-pick <fix-sha>     # or fix directly here
echo "1.1.1" > VERSION
git commit -am "Release 1.1.1"
git tag v1.1.1
git push origin release v1.1.1

git checkout main_v2          # don't lose the fix when 1.2 ships
git merge release
```

## Rules

1. Features go to `main_v2`, never directly to `release`.
2. Tags are only ever cut on `release`.
3. A published tag is **never** moved, deleted, or rebuilt. Wrong release → ship the next number.
4. Published image tags are never deleted — customers who have not upgraded still reference them.
5. Never publish `latest` as the template's `imageTag` default.
