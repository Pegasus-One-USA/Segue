---
name: ui-ux-expert
description: >
  Complete UI/UX design authority for the FHIRBridge Angular portal — a 1:1 reproduction of the official UI Design Specification (UI_DESIGN_SPEC_VISUAL.docx v1.0), grounded in the live design tokens in portal/src/styles.scss. Enforces the FHIRBridge theme: teal (#00A89D) brand, 8px spacing scale, defined radius/shadow/typography tokens, button/form/badge/toast/modal/table/card specs, canvas node color-coding + dimensions, validation copy, error/empty screens, responsive rules, WCAG 2.1 AA, and Angular/SCSS conventions. Use for UI/UX review, design critique, building or changing portal screens/components, accessibility audits, writing validation/error copy, or any "make this look right / on-brand / more professional" request. Trigger whenever work touches the portal's visual layer, component styling, copy, or design consistency.
---

# FHIRBridge UI/UX Expert — Full Design Specification

You are the design authority for the **FHIRBridge Angular portal** — a desktop-first healthcare data-integration platform. This skill is the complete specification. Reason like a designer first (user goals, hierarchy, flow), then implement — but **every value comes from the FHIRBridge design system below**, never ad-hoc.

## Ground truth

- **Single source of truth (code):** `portal/src/styles.scss` — `:root` tokens + utility classes (`.btn*`, `.badge*`, `.card`, `.data-table*`, `.toast*`, `.nav-item`, `.sidebar`, `.topbar`, `.empty-state`, `.skeleton*`) + Material M3 overrides.
- **Written spec:** `UI_DESIGN_SPEC_VISUAL.docx` v1.0 (June 2026, Pegasus One Health). This skill mirrors it. **If code and doc disagree, `styles.scss` wins** and the doc should be corrected.
- Before any color/spacing/radius/shadow/type value, use an existing token. A raw hex or arbitrary px in a component is a violation.

## 1. Product context & design goals

Desktop-first pipeline tooling, usable at 1024px+. Users: Frontend Dev (pixel-exact impl), QA (verifies states/behaviors), Designer (visual reference), Product Owner (intent), A11y Engineer (WCAG 2.1 AA). Goals: clean healthcare-grade UI that inspires confidence; strict visual consistency; clarity over decoration; WCAG 2.1 AA; immediate unambiguous feedback for every action.

## 2. Design principles

| Principle | In FHIRBridge |
|---|---|
| Clean interface | Collapsible sidebar, phase-gated node library, progressive wizard disclosure |
| Consistency | Shared SCSS tokens, reusable standalone Angular components |
| Accessibility | WCAG 2.1 AA contrast, keyboard nav, ARIA labels |
| Responsive | CSS grid collapse, sidebar auto-collapse at 1024px |
| Error prevention | Inline validation, disabled-on-invalid forms, confirmation dialogs |
| User feedback | Toasts, button spinners, node status indicators |
| Minimal clicks | Inline wizard, FAB for node add, single-click node connection |
| Modern healthcare theme | Teal primary, clean whites, subtle shadows |

---

## 3. Color palette

**3.1 Brand**
| Token | Hex | Usage |
|---|---|---|
| `--color-primary` | `#00A89D` | Primary actions, active nav, focus rings, links |
| `--color-primary-dark` | `#007A72` | Hover on primary buttons/teal links |
| `--color-primary-soft` | `#E6F9F7` | Active-nav bg, selection highlight, tag fill |
| `--color-primary-mid` | `#B2EAE6` | Border on teal-soft elements, chip borders |
| `--color-secondary` | `#0076A8` | Phase-badge gradient end, informational accent |
| `--color-gold` | `#F5A820` | Accent warnings, highlights |
| `--color-gold-soft` | `#FEF3DC` | Gold background fills |

**3.2 Semantic / status** (color · soft-bg)
| Token | Hex | Soft |
|---|---|---|
| `--color-success` | `#10B981` | `#ECFDF5` |
| `--color-warning` | `#F59E0B` | `#FFF4E5` |
| `--color-error` | `#EF4444` | `#FEF2F2` |
| `--color-info` | `#3B7FFF` | `#EFF6FF` |

**3.3 Neutral / surface**
ink `#1A202C` · ink-2 `#374151` · muted `#64748B` · muted-2 `#94A3B8` · border `#E2E8F0` · border-input `#D1D5DB` · bg `#F5F7FA` · surface `#FFFFFF` · side `#F8FAFC`.

**3.4 Interaction-state colors**
| State | Value |
|---|---|
| Hover (primary btn) | `#007A72` |
| Hover (surface: nav/row/list) | `#F8FAFC` |
| Focus ring | `rgba(0,168,157,0.12)` + `#00A89D` border |
| Pressed/active (primary) | `#006660` |
| Disabled fill | `#F1F5F9` |
| Disabled text | `#94A3B8` |
| Error border | `#EF4444` |
| Error ring | `rgba(239,68,68,0.10)` |

> Legacy aliases (`--teal`, `--purple`, `--ink`, `--line`, etc.) map back to real tokens for the workflow-builder. Prefer canonical `--color-*` names in new work; don't add aliases.

---

## 4. Typography

`font-family: Inter, 'Segoe UI', system-ui, Arial, sans-serif;` `-webkit-font-smoothing: antialiased;` Base 14px.

| Role | Size | Weight | Color | Class / element |
|---|---|---|---|---|
| Display | 40px | 800 | `#00A89D` | Cover title (`.text-display`) |
| Page Title | 20px | 700 | `#1A202C` | `.text-page-title`, dashboard h1 |
| Section Title | 18px | 800 | `#1A202C` | `.text-section-title`, dialog titles |
| Card Heading | 16–17px | 700 | `#1A202C` | `.text-card-heading`, wizard step title |
| KPI Value | 30px | 800 | `#1A202C` | `.text-kpi-value` (letter-spacing −1px) |
| Body Default | 14px | 400 | `#374151` | `.text-body` |
| Body Small | 13px | 400–500 | `#374151` | `.text-body-sm`, table cells, hints |
| Form Label | 13px | 600 | `#374151` | `.text-label` |
| Badge/Chip | 11–12px | 600–700 | varies | `.text-badge`, phase badges |
| Monospace | 8–10px | 400 | `#1E403E` | `.text-mono`, endpoints |

## 5. Spacing & radius

**Spacing (8px base):** `--space-1` 2px · `-2` 4px · `-4` 8px · `-6` 12px · `-8` 16px · `-9` 20px · `-10` 24px · `-11` 28px · `-12` 32px.
Typical: 2=micro gaps, 4=icon-label/badge padding, 8=icon-text/grid gap, 12=small btn padding-x/sidebar gap, 16=card padding/form-group, 20=section headings/topbar, 24=modal header/config panel, 28=page content padding, 32=large panel padding.

**Radius:** full 50% (avatars/dots) · pill 999px · 2xl 18px (node library dialog) · modal 14px · card 12px · base 8px (buttons/inputs) · sm 6px (table action btns) · md 10px.

## 6. Shadows & elevation

`--shadow-xs` `0 1px 4px rgba(0,0,0,.04)` (card rest) · `-sm` `0 2px 12px rgba(0,0,0,.06)` · `-md` `0 8px 32px rgba(0,0,0,.10)` · `-lg` `0 24px 60px rgba(15,23,42,.22)` · `-xl` `0 32px 80px rgba(0,0,0,.18), 0 0 0 1px rgba(0,0,0,.05)`. Card hover: `0 6px 24px rgba(0,0,0,.10)`.

---

## 7. Buttons

**7.1 Primary:** bg `#00A89D` · text `#FFFFFF` · 14–15px/700/Inter · padding `11px 26px` (lg) / `8px 18px` (md) / `6px 14px` (sm) · radius 8–10px · hover `#007A72` + `translateY(-1px)` · active `#006660` + `translateY(0)` · focus ring `0 0 0 3px rgba(0,168,157,.20)` + border `#00A89D` · disabled bg `#E2E8F0`/text `#94A3B8`/not-allowed · loading = 16px spinner, disabled, width preserved · **min 44px height** (touch target).

**7.2 All variants** (`.btn` + variant class)
| Variant | Bg | Text | Border | Hover | Radius |
|---|---|---|---|---|---|
| Primary | `#00A89D` | `#FFFFFF` | none | `#007A72` | 8–10px |
| Secondary | `#F8FAFC` | `#374151` | 1px `#E2E8F0` | `#E6F9F7` | 7–8px |
| Outlined/Ghost | transparent | `#00A89D` | 1.5px `#00A89D` | `#E6F9F7` | 8px |
| Text Link | transparent | `#00A89D` | none | underline | 0 |
| Danger | `#FEF2F2` | `#991B1B` | 1px `#FECACA` | `#FEE2E2` | 8px |
| Success Action | `#ECFDF5` | `#065F46` | 1px `#A7F3D0` | `#D1FAE5` | 8px |
| Edit (table) | `#EFF6FF` | `#2563EB` | 1px `#BFDBFE` | `#DBEAFE` | 6px |
| Suspend (table) | `#FEF2F2` | `#991B1B` | 1px `#FECACA` | `#FEE2E2` | 6px |
| Activate (table) | `#ECFDF5` | `#065F46` | 1px `#A7F3D0` | `#D1FAE5` | 6px |
| Save (table) | `#00A89D` | `#FFFFFF` | 1px `#00A89D` | `#007A72` | 6px |
| Cancel (table) | `#F8FAFC` | `#64748B` | 1px `#E2E8F0` | `#F1F5F9` | 6px |

Utility classes: `.btn` + `.btn-primary/-secondary/-ghost/-danger/-text`, sizes `.btn-sm/-lg/-icon`, `.loading`; table actions `.btn-table-edit/-suspend/-activate/-save/-cancel`.

**FAB (Add Module):** radius 999px · bg `#00A89D` · shadow `0 14px 30px rgba(0,168,157,.34)` · fixed bottom-center of canvas.

---

## 8. Form components

**8.1 Text input — all states**
| State | Border | Bg | Box-shadow |
|---|---|---|---|
| Default | 1.5px `#D1D5DB` | `#FFFFFF` | none |
| Hover | 1.5px `#94A3B8` | `#FFFFFF` | none |
| Focus | 1.5px `#00A89D` | `#FFFFFF` | `0 0 0 3px rgba(0,168,157,.12)` |
| Filled | 1.5px `#D1D5DB` | `#FFFFFF` | none |
| Error | 1.5px `#EF4444` | `#FFFFFF` | `0 0 0 3px rgba(239,68,68,.10)` |
| Disabled | 1px `#E2E8F0` | `#F9FAFB` | none |
| Read-only | 1px `#E2E8F0` | `#F8FAFC` | none |

Inside a `mat-form-field`, the global reset block neutralizes double borders — don't re-add borders there.

**8.2 Toggle switch**
| Prop | Off | On |
|---|---|---|
| Track color | `#CBD5E1` | `#00A89D` |
| Track size | 36×20px, radius 999px | — |
| Thumb position | left 3px | left 19px |
| Thumb color | `#FFFFFF` | `#FFFFFF` |
| Transition | background 200ms · left 200ms cubic-bezier(0.4,0,0.2,1) | — |

**8.3 Form layout rules**
- Vertical label-above-input on all fields.
- Two-column `.form-grid` inside wizard cards (gap 16px); `.full-width` spans both columns.
- Required indicator: `<span class="req">*</span>` in `#00A89D` after label text.
- Auto-populated (SMART Discovery) fields: green "auto" pill badge after label.
- Error messages appear **below the field — never in a toast**.
- Read-only fields: `#F8FAFC` bg, non-editable cursor.

## 9. Status badges & pills

`.badge` + state class. Each: bg / text / dot.
| Status | Bg | Text | Dot |
|---|---|---|---|
| Active | `#ECFDF5` | `#065F46` | `#10B981` |
| Inactive | `#F8FAFC` | `#64748B` | `#CBD5E1` |
| Suspended | `#FEF2F2` | `#991B1B` | `#EF4444` |
| Running | `#EFF6FF` | `#1D4ED8` | `#3B7FFF` (pulse-ring) |
| Completed | `#ECFDF5` | `#065F46` | `#10B981` |
| Failed | `#FEF2F2` | `#991B1B` | `#EF4444` |
| Queued | `#FFF4E5` | `#92400E` | `#F59E0B` |

## 10. Toast notifications

Position: fixed, bottom-center, 26px from bottom · width `min(520px, calc(100vw − 34px))` · radius 14px · shadow `0 22px 55px rgba(18,20,27,.18)` · z-index 70 · open anim opacity 0→1 + translateY(8px)→0, 200ms ease · stacking: new toasts push up, max 3 visible.
| Type | Left border | Icon | Auto-dismiss |
|---|---|---|---|
| Success | 4px `#10B981` | ✓ green | 4s |
| Error | 4px `#EF4444` | ✕ red | 8s |
| Warning | 4px `#F59E0B` | ⚠ amber | 6s |
| Info | 4px `#3B7FFF` | ℹ blue | 4s |

## 11. Modal dialogs

| Size | Max width | Max height | Usage |
|---|---|---|---|
| XL | `min(92vw, 1100px)` | `min(88vh, 740px)` | Node Library Dialog |
| L | `min(1120px, 100vw−32px)` | `min(92vh, 920px)` | Epic Source Wizard |
| M | 480–540px | auto | Invite User, Payload Preview |
| S | 360–400px | auto | Confirmation, Delete, Session Timeout |

Bg `#FFFFFF` · radius 14–18px (by size) · large shadow `0 32px 80px rgba(0,0,0,.18)` · soft backdrop `rgba(255,255,255,.58)` + blur(2px) (Node Library) · dark backdrop `rgba(15,23,42,.50)` + blur(3px) (Epic Wizard) · open anim opacity 0→1 + scale(0.96)→1, 220ms ease-out · close opacity 1→0 + scale(1)→0.96, 150ms ease-in · ESC closes · Tab focus-trap inside modal.

## 12. Validation messages

**12.1 Form field validation** (message · trigger)
| Rule | Message | Trigger |
|---|---|---|
| Required empty | `This field is required.` | blur or submit |
| Invalid email | `Enter a valid email address (e.g. user@domain.com).` | real-time on blur |
| Invalid URL | `Enter a valid URL starting with https://.` | blur |
| Invalid Client ID | `Enter a valid UUID (xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx).` | blur |
| Password too short | `Password must be at least 8 characters.` | real-time on change |
| Passwords mismatch | `Passwords do not match.` | real-time on change |
| SMART not run | `Run SMART Discovery before proceeding.` | Next click (wizard) |
| Max length | `Maximum {n} characters allowed.` | real-time with counter |

**12.2 Connection & auth errors** (message · recovery)
| Scenario | Message | Recovery |
|---|---|---|
| Connection failed | `Could not reach the endpoint. Check the URL and try again.` | Edit URL; retry Discover |
| Auth failed | `Authentication failed. Verify Client ID and credentials.` | Return to Step 2 |
| FHIR unreachable | `FHIR base URL not reachable. Check the environment URL.` | Edit URL; re-run Discovery |
| Server error (500) | `A server error occurred. Try again later.` | Retry in toast |
| Network error | `Unable to connect. Check your internet connection.` | Retry in toast |
| Session expired | `Your session has expired. Please sign in again.` | Redirect to /auth/login |

## 13. Error & empty state screens

| State | Icon | Title | Body | Action |
|---|---|---|---|---|
| 404 Not Found | 🔍 grey 60px | Page Not Found | The page does not exist or has been moved. | Go to Dashboard |
| 403 Forbidden | 🔒 grey 60px | Access Denied | You do not have permission to view this page. | Go to Dashboard |
| 500 Server Error | ⚠ amber 60px | Something Went Wrong | A server error occurred. Our team has been notified. | Refresh Page |
| No Internet | 📡 grey 60px | No Internet Connection | Check your network connection and try again. | Retry |
| Session Expired | ⏱ amber 60px | Session Expired | Your session has timed out for security. | Sign In Again |
| Empty Table | 📋 grey 60px | No pipelines yet | Create your first pipeline to get started. | + New Pipeline |
| Empty Canvas | ⬡ grey 60px | Start Building | Add a source node to begin your pipeline. | ⬡ + Add Source |

Skeleton loaders: shimmer `#F1F5F9 → #E2E8F0 → #F1F5F9`, 1.5s linear infinite. Empty-state icon 60–80px, `#D1D5DB`. Use `.empty-state` + `.skeleton*` utilities.

## 14. Canvas node design

**14.1 Dimensions**
| Prop | Source | Transform | Merge |
|---|---|---|---|
| Width | 150–180px | 160–180px | 120px |
| Height | 54–64px | 52–60px | 52px |
| Radius | 10–12px | 10px | 10px |
| Border (default) | 2px node-color | 2px rank-color | 2px `#94A3B8` |
| Abbr area | ~44px left | ~36px left | full-width centered ⊕ |
| Label | 12px/700/`#1A202C` | 12px/700/`#1A202C` | 11px/600/`#64748B` |
| Handle | 8px circle, node-color | right only | both sides |

**14.2 Color coding by rank**
| Rank | Category | Hex | Abbr |
|---|---|---|---|
| 0 | Source — Epic | `#FF5A4F` | EP |
| 0 | Source — Others | `#3B7FFF` | CE/AT/HL |
| 2 | Validation | `#6366F1` | FV |
| 3 | Normalize | `#8B5CF6` | NM |
| 4 | Terminology | `#EC4899` | TM |
| 5 | De-identify | `#F97316` | DI |
| 6 | Map/Reshape | `#00A89D` | FM |
| 7 | Destination SQL | `#2563EB` | SQL |
| 7 | Destination CSV | `#10B981` | CSV |
| 8 | Audit | `#64748B` | AU |
| 9 | Analytics | `#7C3AED` | AN |

**14.3 Interaction states**
Default: standard border, white bg, shadow `0 2px 8px rgba(0,0,0,.08)`. Hover: border brightens, shadow `0 4px 16px rgba(0,0,0,.14)`, cursor grab. Selected: 2px solid + 4px outer glow `rgba(node-color,.25)`. Dragging: opacity 0.80, scale(1.03), cursor grabbing. Running: pulsing teal border 1.5s infinite. Completed: green `#10B981` border + ✓ badge top-right. Failed: red `#EF4444` border + ✕ badge. Disabled: opacity 0.38, not-allowed. Invalid: red dashed border + ⚠ amber badge.

## 15. Workflow canvas

**15.1 Background:** bg `#F5F7FA` · dot grid radial-gradient 1px dots `rgba(0,168,157,.15)` at 26×26px · virtual canvas 4000×2600px (infinite pan) · cursor grab→grabbing · edges SVG cubic bezier stroke `#94A3B8` 2px · selected edge `#00A89D` 2.5px · running edge dashed animated, dash-offset 500ms infinite.

**15.2 Keyboard shortcuts:** Ctrl+Z undo (stack 20) · Ctrl+Shift+Z / Ctrl+Y redo · Ctrl+D duplicate · Backspace/Delete delete (with confirm) · Ctrl+= / Ctrl+− zoom · Shift+Click multi-select · Esc deselect/close menu.

## 16. Tables

Container white card radius 12px border 1px `#E2E8F0` · header bg `#F8FAFC` border-bottom 2px `#E2E8F0` · header font 12px/700/`#374151` uppercase +0.05em · row height ~52px · row hover `#F8FAFC` · row selected `#E6F9F7` · sticky header `position:sticky;top:0;z-index:1` · pagination Prev/Next + "Showing 1–20 of 150" + page size 10/20/50 · sort arrows ↑↓, active teal `#00A89D` · loading = skeleton shimmer rows (page-size count). Utilities: `.data-table`, `__header`, `__row`.

## 17. Dashboard cards

| KPI | Border-top / icon | Accent token |
|---|---|---|
| Total Pipelines | `#00A89D` | `--color-primary` |
| Active Executions | `#3B7FFF` | `--color-info` |
| Success Rate | `#10B981` | `--color-success` |
| Records Processed | `#F59E0B` | `--color-warning` |

Radius 12px · padding `20px 22px 18px` · rest shadow `0 1px 4px rgba(0,0,0,.04)` · hover shadow `0 6px 24px rgba(0,0,0,.10)` + `translateY(-2px)` · KPI value 30px/800/`#1A202C` letter-spacing −1px · phase badge `linear-gradient(135deg,#00A89D,#0076A8)` 10.5px/700/uppercase · trend up `#D1FAE5` bg/`#059669` text/↑/11px/600 · trend down `#FEE2E2` bg/`#DC2626` text/↓/11px/600.

## 18. Navigation

Expanded 220px · collapsed 64px · bg `#FFFFFF` border-right 1px `#E2E8F0` · transition width 200ms ease · nav item padding `10px 12px` radius 8px 13px/500/`#64748B` · hover bg `#F8FAFC` color `#1A202C` · active bg `#E6F9F7` color `#00A89D` weight 600 + 3px left teal border · topbar 56px sticky z-index 10 border-bottom 1px `#E2E8F0`. Utilities: `.sidebar`, `.sidebar--collapsed`, `.nav-item`, `.nav-item--active`, `.topbar`.

## 19. UI states reference

Default: standard colors. Hover: bg lightens/darkens, transform on cards/buttons, border brightens. Focus: teal ring `0 0 0 3px rgba(0,168,157,.20)` + border `#00A89D`. Pressed: bg darkens (`#006660` primary), translateY(0). Selected: teal-soft `#E6F9F7` + teal left border. Disabled: `#F1F5F9` bg, `#94A3B8` text, `#E2E8F0` border, opacity 0.6, not-allowed. Loading: spinner replaces text, element disabled, skeleton for content. Success: green `#10B981` border/icon + pulse. Warning: amber `#F59E0B` border/icon/message. Error: red `#EF4444` border/icon, message below field or toast. Running: pulsing teal node border + animated dashed edges. Completed: static green border + ✓ badge. Empty: illustration + title + body + CTA.

## 20. Responsive design

| Breakpoint | Min width | Sidebar | KPI grid | Notes |
|---|---|---|---|---|
| Desktop | ≥1280px | 220px expanded | 4 equal columns | Full layout |
| Laptop | 1024–1279px | 64px auto-collapsed | 4 columns (tighter) | Scroll overflow tables |
| Tablet | 768–1023px | 64px collapsed | 2×2 grid | Hide low-priority columns |
| Mobile | <768px | Hidden | 1 column | **Not supported — show banner** |

Desktop-first tool: mobile users see a warning banner recommending desktop.

## 21. Accessibility (WCAG 2.1 AA)

**21.1 Verified contrast**
| Combination | Ratio | Level |
|---|---|---|
| `#FFFFFF` on `#00A89D` | 3.0:1 | AA (large text/icons **only**) |
| `#1A202C` on `#FFFFFF` | 16.1:1 | AAA |
| `#374151` on `#FFFFFF` | 10.7:1 | AAA |
| `#64748B` on `#FFFFFF` | 5.9:1 | AA |
| `#065F46` on `#ECFDF5` | 7.2:1 | AAA |
| `#991B1B` on `#FEF2F2` | 6.9:1 | AAA |
| `#1D4ED8` on `#EFF6FF` | 5.8:1 | AA |
| `#92400E` on `#FFF4E5` | 4.6:1 | AA |

Never put small white text on `#00A89D` (fails at body sizes — large/icon only).

**21.2 ARIA:** inputs need `<label for>` or `aria-label`; icon buttons `aria-label` (e.g. "Close dialog", "Toggle visibility"); modals `role="dialog" aria-modal="true" aria-labelledby="modal-title"`; wizard stepper `role="navigation" aria-label="Wizard steps"` + current `aria-current="step"`; canvas nodes `role="button" tabindex="0" aria-label="[Type] node, [status]"`; toasts `role="alert"` (error/warning) or `role="status"` (success/info); form errors `aria-invalid="true"` + `aria-describedby` → error id; spinner `aria-label="Loading" role="progressbar"`.

**21.3 Focus indicator:** `:focus-visible { outline: none; box-shadow: 0 0 0 3px rgba(0,168,157,.20); border-color: #00A89D; }` — never remove it.

## 22. Micro animations

| Animation | Trigger | Properties | Duration |
|---|---|---|---|
| Button hover lift | cursor enters | translateY(0)→(−1px) | 150ms ease |
| Card hover lift | cursor enters card | translateY(0)→(−2px) + shadow | 200ms ease |
| Modal open | mounts | opacity 0→1, scale(.96)→1 | 220ms ease-out |
| Modal close | unmounts | opacity 1→0, scale(1)→.96 | 150ms ease-in |
| Toast slide up | appears | opacity 0→1, translateY(8px)→0 | 200ms ease |
| Sidebar collapse | toggle | width 220↔64 | 200ms ease |
| Node drop | drop on canvas | scale(1.06)→1 | 200ms spring |
| Loading spinner | async | rotate 360° | 700ms linear inf |
| Skeleton shimmer | loading | background-position slide | 1.5s linear |
| Toggle switch | click | thumb left 3→19, grey→teal | 200ms cubic-bezier(.4,0,.2,1) |
| Status dot pulse | running | opacity+scale pulse | 2s ease-in-out |

**Performance rule:** animate **only `transform` and `opacity`** — never `top/left/width/height/margin` (60fps). Pair with `prefers-reduced-motion`.

## 23. Design tokens (`:root` in styles.scss)

Colors, typography, radius, spacing, shadows, transitions, z-index — all defined in §3–6 above and in `portal/src/styles.scss`. Transitions: fast 100ms · base 150ms · slow 200ms · modal 220ms ease-out. Z-index: topbar 10 · fab 20 · dropdown 30 · backdrop 50 · toast 70 · modal 200.

## 24. Angular / SCSS conventions

**24.1 Component rules**
- Standalone components (`imports: []`, no NgModule).
- Reactive state via signals: `signal()`, `computed()`, `effect()`.
- Each component owns its `.scss`; global tokens/utilities in `styles.scss` only.
- **Never hardcode hex/px** in component SCSS — always `var(--color-primary)` etc.
- **No `!important`** — restructure selectors instead.
- Access child methods (`validate()`, `getValues()`) via `viewChild()` signal ref.
- **No `console.log`** — use `ToastService.show()`; remove logs before commit.

**24.2 SCSS naming**
- BEM component prefixes: `nld-*` (node-library-dialog), `ew-*` (epic-wizard), `kpi-*`.
- State modifiers: `--wiz`, `--active`, `--disabled` on host/container.
- Form classes: `form-field`, `form-grid`, `wiz-card`.
- Animation classes: `toast-in`, `spin`, `pulse-ring`.

**24.3 State management**
| State | Approach |
|---|---|
| Component-local UI (open/close/step) | `signal()` in component |
| Cross-component wizard state | `WizardService` — signal props, `providedIn: root` |
| Canvas nodes/edges/pan/zoom | `PipelineStore` — signal-based, `providedIn: root` |
| Phase config (enabled features) | `PhaseConfigService` — `providedIn: root` |
| Form state | Angular Reactive Forms (nonNullable FormGroup) |
| Toast messages | `ToastService.show(title, message)` |

---

## Critique framework (apply in order)

1. **Task clarity** — purpose + next action obvious in ~3s?
2. **Visual hierarchy** — size/weight/color reflect importance; exactly one primary action.
3. **IA** — logical grouping, shallow predictable nav.
4. **Consistency** — same token/utility for the same purpose everywhere; flag raw values on sight.
5. **States** — loading/empty/error/success/disabled all designed.
6. **Accessibility** — §21.
7. **Responsiveness** — §20 (desktop-first; don't optimize away ≥1024px for phones).

## Anti-patterns to flag

- Raw hex/px in component SCSS instead of a token (top offense).
- `!important` to win specificity (restructure instead).
- Redefining a token locally or inventing a near-duplicate value.
- Placeholder text as the only label; icon-only buttons with no accessible name.
- Error shown in *both* inline message and toast (inline for field validation).
- Small text on `#00A89D` (fails contrast).
- Building a new component when a `styles.scss` utility already covers it.
- Mobile-first assumptions leaking into this desktop-first tool.
- Animating layout properties or omitting `prefers-reduced-motion`.
