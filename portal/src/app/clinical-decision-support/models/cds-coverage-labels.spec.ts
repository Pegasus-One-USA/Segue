import {
  cdsCoverageDecisionLabel,
  cdsCoveredLabel,
  cdsDocNeededLabel,
  cdsImportActionLabel,
  cdsPaNeededLabel,
  cdsRuleSummary,
} from './cds-coverage-labels';

describe('cds-coverage-labels', () => {
  it('names each code in plain words and keeps an unknown code as it is', () => {
    expect(cdsCoveredLabel('not-covered')).toBe('Not covered');
    expect(cdsCoveredLabel(null)).toBe('Not stated');
    expect(cdsPaNeededLabel('auth-needed')).toBe('Prior authorization needed');
    expect(cdsPaNeededLabel('satisfied')).toBe('Prior authorization already approved');
    expect(cdsDocNeededLabel(null)).toBe('No documentation');
    expect(cdsPaNeededLabel('something-new')).toBe('something-new');
    expect(cdsImportActionLabel('unchanged')).toBe('No change');
  });

  it('reads an Activity decision ("-" means not stated)', () => {
    expect(cdsCoverageDecisionLabel('covered/auth-needed/clinical')).toBe('Covered, prior authorization needed, clinical documentation');
    expect(cdsCoverageDecisionLabel('not-covered/-/-')).toBe('Not covered');
    expect(cdsCoverageDecisionLabel('-/-/-')).toBe('Coverage not stated');
    expect(cdsCoverageDecisionLabel('conditional')).toBe('Covered under conditions');
  });

  it('summarises a rule', () => {
    expect(cdsRuleSummary({ covered: 'covered', paNeeded: 'no-auth', docNeeded: null })).toBe('Covered, no prior authorization');
  });
});
