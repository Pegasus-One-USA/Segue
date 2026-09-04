// ── Auto-sizing helper for the source-tree and target-table cards ──────────────
// Both cards default to a fixed width today (400px / 300px) then rely entirely on the user's own
// drag-resize for anything wider. This computes a one-time default from the longest label actually in
// the card instead, so a card with short field/column names doesn't waste canvas space and one with a
// long FHIR path or column name doesn't immediately need a manual resize just to read it. Applied only
// once at mount (see each card's ngAfterViewInit) — never re-applied afterward, so it can't fight a
// manual drag-resize once the user has made one.

const CHAR_WIDTH_PX = 6.5;

export function autoCardWidth(labelLengths: number[], basePaddingPx: number, minWidth: number, maxWidth: number): number {
  const longest = labelLengths.reduce((max, len) => Math.max(max, len), 0);
  const width = basePaddingPx + longest * CHAR_WIDTH_PX;
  return Math.min(maxWidth, Math.max(minWidth, Math.round(width)));
}
