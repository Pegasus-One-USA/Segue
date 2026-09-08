/**
 * Display names for `SourceSystemType` enum members whose marketed brand differs from the identifier the backend
 * stores. Everything else renders as-is, so this table stays small on purpose — it is an exception list, not a
 * translation layer.
 *
 * The enum member is what the API sends and what the filter posts back, so it must NOT change here: only what a
 * user reads changes. `Healow` is the clearest case — eClinicalWorks markets the product as eCW, and "Healow" is
 * its patient-app brand, so a Source badge reading "Healow" on an eCW workflow looks like the wrong vendor
 * entirely. The node-library form already labels this vendor 'eCW' (see healow-source-form.component.html's
 * `vendorLabel`); this is the same decision applied where the enum reaches a list.
 */
const SOURCE_SYSTEM_DISPLAY_NAMES: Readonly<Record<string, string>> = {
  Healow: 'eCW',
};

/**
 * The brand name to show for a `SourceSystemType`. Unmapped values (Epic, Cerner, Athenahealth, …) already read
 * correctly and pass through untouched, as do null/empty values, which callers render as their own placeholder.
 */
export function sourceSystemDisplayName(sourceSystemType: string | null | undefined): string {
  if (!sourceSystemType) return '';
  return SOURCE_SYSTEM_DISPLAY_NAMES[sourceSystemType] ?? sourceSystemType;
}
