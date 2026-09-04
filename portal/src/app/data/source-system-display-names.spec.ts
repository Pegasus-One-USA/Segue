import { sourceSystemDisplayName } from './source-system-display-names.data';

// The Source badge used to render the raw SourceSystemType enum member, so an eClinicalWorks workflow showed
// "Healow" — eCW's patient-app brand, which reads like a different vendor entirely.
describe('sourceSystemDisplayName', () => {
  it('shows eCW for the Healow enum member', () => {
    expect(sourceSystemDisplayName('Healow')).toBe('eCW');
  });

  it('passes through vendors whose enum member already reads correctly', () => {
    expect(sourceSystemDisplayName('Epic')).toBe('Epic');
    expect(sourceSystemDisplayName('Cerner')).toBe('Cerner');
    expect(sourceSystemDisplayName('Athenahealth')).toBe('Athenahealth');
  });

  it('is case-sensitive so it cannot collide with an unrelated value', () => {
    // The API sends the exact enum member; a loose match risks rewriting something that only looks similar.
    expect(sourceSystemDisplayName('healow')).toBe('healow');
  });

  it('returns empty for a missing value so callers can apply their own placeholder', () => {
    expect(sourceSystemDisplayName(null)).toBe('');
    expect(sourceSystemDisplayName(undefined)).toBe('');
    expect(sourceSystemDisplayName('')).toBe('');
  });
});
