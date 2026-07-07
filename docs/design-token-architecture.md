# FHIRBridge Design Token Architecture

Status: **implemented** (light + dark) · Owner: Portal · Source of truth: `portal/src/styles.scss`

This document describes the tiered design-token architecture for the FHIRBridge Angular portal and how theming works. It complements `UI_DESIGN_SPEC_VISUAL.docx` (which documents the *values*) and the `ui-ux-expert` skill (which enforces usage).

## Why tiers

Previously the portal had a **single flat layer**: semantic tokens (`--color-primary: #00A89D`) held raw hex directly. That works, but it can't be themed (change one value → no ripple) and offers no place for a palette. The architecture now has three tiers plus a framework bridge:

```
TIER 1  Primitives     --ref-teal-500: #00A89D          raw palette; the ONLY hex literals
   │                                                     (never referenced by components)
TIER 2  Semantic       --color-primary: var(--ref-teal-500)   intent-named; components use THIS
   │                                                     (this tier is what a theme overrides)
TIER 3  Component       --surface-raised: var(--color-surface)  role-scoped; optional convenience
   │
BRIDGE  Material M3    --mat-sys-primary: var(--color-primary)  Angular Material follows the theme
```

**Rule of consumption:** components reference **Tier 2** (`--color-*`, `--space-*`, `--radius-*`, …) or **Tier 3** (`--surface-*`). Components must **never** reference Tier 1 primitives or hardcode hex. Primitives change rarely; semantics are the stable contract.

## Where each tier lives

All in `portal/src/styles.scss`:

- **Tier 1 — Primitives** (`--ref-*`): theme-independent, defined once in `:root`. Palette ramps (`--ref-teal-50…900`, `--ref-neutral-50…900`, status hues, `--ref-coral-*` for Epic).
- **Theme-independent tokens**: `--font-family`, `--space-*`, `--radius-*`, `--shadow-*`, `--transition-*`, `--z-*` — in `:root`, not themed (geometry/motion don't change per theme; a few shadow tokens are darkened in dark mode).
- **Tier 2 — Semantic** (`--color-*`): light values in `:root`, dark overrides in `html[data-theme="dark"]`.
- **Tier 3 — Component** (`--surface-*`, `--border-default`): reference Tier 2. Adoption in progress — page background and cards are wired; broader adoption tracked with the token-compliance cleanup.
- **Bridge** (`--mat-sys-*`): color tokens reference Tier 2 so Material components (paginator, selects, menus, form fields) follow the theme automatically. Font tokens force Inter (the prebuilt Azure-blue theme ships Roboto).

## Theming

Dark mode is activated by `data-theme="dark"` on `<html>`. Because only the **semantic tier** is overridden, everything downstream (component tokens, Material bridge, all `var(--color-*)` consumers) cascades automatically — no component changes needed.

Selector specificity: `html[data-theme="dark"]` (0,1,1) beats `:root` (0,1,0), so dark wins cleanly without `!important`.

### ThemeService

`portal/src/app/services/theme.service.ts` (`providedIn: 'root'`, signal-based):

- `mode` signal: `ThemeMode` = `'light' | 'dark' | 'system'` (plus any theme ids you add), persisted in `localStorage` under `fhirbridge.theme`.
- `resolved` signal: `AppliedTheme` (= `ThemeMode` minus `'system'`) — the theme actually applied after resolving `'system'` against `prefers-color-scheme`.
- `set(mode)`, `toggle()`. Reacts to OS theme changes when in `'system'` mode.
- `apply()` is **N-theme**: `light` = the `:root` default (attribute removed); every other theme is applied as `data-theme="<id>"`. Adding a theme needs no change here.
- Applied at startup via injection in `AppComponent`. **Default is `'light'`**; dark is opt-in.

The Settings → Preferences theme cards and the user-menu toggle are wired to `ThemeService` (through `UserProfileService`, which delegates to it). To trigger a theme elsewhere, inject `ThemeService` and call `set('dark')` / `toggle()`.

### Adding a new theme

1. Add an `html[data-theme='<id>'] { … }` block in `styles.scss` overriding the **semantic tier** (mirror the dark block; reuse primitives, add new ones if needed).
2. Add `'<id>'` to `ThemeMode` in `theme.service.ts`.
3. *(optional)* add a card to `themeOptions` in `preferences.component.ts` and the id to `AppTheme` in `user-profile.model.ts`.
4. Run the WCAG contrast audit on the new palette; `npx ng build` to verify.

No component or `apply()` changes are required.

## Invariants (do not break)

1. **Light theme is byte-identical to before this refactor** — every semantic token resolves to the same hex it did as a flat value. Verified by comparing computed values in the built `styles.css`.
2. **No component references Tier 1** — primitives are private to `styles.scss`.
3. **No `!important` introduced** — theming relies on selector specificity.
4. **Semantic names are stable** — Tier 2 token names are the public contract; renaming one is a breaking change across ~80 component stylesheets.

## First-pass dark palette — pending design review

The dark values in `html[data-theme="dark"]` are a credible first pass (inverted neutrals, brand lifted for contrast, status hues brightened with dark-tinted soft backgrounds). A **WCAG 2.1 AA contrast audit** has been run: all sampled text/background pairs pass AA (most AAA). `--color-muted-2` was lifted to `#8493AA` (4.69:1), and flip-aware `--color-*-strong` foreground tokens were added so badges and table-action buttons stay legible on dark. Still open before GA: promote the dark raw hex to **dark primitives** (`--ref-*` for dark) for tier discipline, and tokenize the remaining hardcoded **border tints** on danger/table buttons.

## Enforcement (CI + local)

Compliance is enforced by two **ratchets** (no-regression: existing debt is baselined; only increases fail):

- **Token compliance** — `portal/scripts/check-tokens.mjs` (baseline `scripts/token-baseline.json`): 0 `var(--ref-*)` in components, and no rise in hardcoded hex or `!important`.
- **Lint** — `portal/scripts/lint-ratchet.mjs` (baseline `scripts/lint-baseline.json`): ESLint (`@angular-eslint` + `typescript-eslint`) and stylelint (`stylelint-config-recommended-scss`).

Both run in `.github/workflows/portal-ci.yml` on PRs to `main`, and locally via `.githooks/pre-push` (enabled by `npm install`'s `prepare` script, which sets `core.hooksPath`). After a cleanup lowers counts, re-baseline with `--update-baseline` and commit. Making the CI check block merges requires a branch-protection rule on `main` requiring the `build` check.

## Follow-ups

- Promote dark raw values → dark primitives (tier discipline).
- Finish Tier-3 component-token adoption across utility classes (tracked with the token-compliance cleanup: one-off hexes + `!important` removal).
- Burn down the baselined lint debt (ESLint 202, stylelint 17 — several are real CSS bugs) and re-baseline.
- Tokenize the remaining hardcoded border tints on danger/table-action buttons for full dark fidelity.
