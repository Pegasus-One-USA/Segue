import {
  CDS_DEFAULT_OVERRIDE_REASONS,
  buildCdsFixesGroup,
  buildCdsLinksArray,
  buildCdsReasonsArray,
  cdsFixesInvalid,
  cdsFixesNoTypeOn,
  cdsSavedOptions,
  makeCdsLinkGroup,
  makeCdsMappingGroup,
  makeCdsReasonGroup,
  readCdsFixes,
  readCdsLinks,
  readCdsReasons,
} from './cds-service-options.util';
import { makeCreateFixType, makeDeleteFixType, makeService } from '../testing/cds-test-data';

describe('cds-service-options.util', () => {
  describe('dismiss reasons', () => {
    it('starts empty (the standard reasons) unless the service has its own list', () => {
      expect(buildCdsReasonsArray(null).length).toBe(0);
      expect(buildCdsReasonsArray(makeService({
        overrideReasonsAreDefault: true, overrideReasons: [...CDS_DEFAULT_OVERRIDE_REASONS],
      })).length).toBe(0);

      const own = buildCdsReasonsArray(makeService({
        overrideReasonsAreDefault: false, overrideReasons: [{ code: 'pt-refused', display: 'Patient refused' }],
      }));
      expect(own.getRawValue()).toEqual([{ code: 'pt-refused', display: 'Patient refused' }]);
    });

    it('accepts only letters, digits, hyphens and underscores in a code, up to 50', () => {
      const valid = ['abc', 'A_b-1', 'x'.repeat(50)];
      const invalid = ['', 'has space', 'dot.ted', 'x'.repeat(51), 'é'];
      for (const code of valid) expect(makeCdsReasonGroup({ code, display: 'd' }).valid).withContext(code).toBeTrue();
      for (const code of invalid) expect(makeCdsReasonGroup({ code, display: 'd' }).valid).withContext(code).toBeFalse();
      expect(makeCdsReasonGroup({ code: 'ok', display: '' }).valid).toBeFalse();
      expect(makeCdsReasonGroup({ code: 'ok', display: 'x'.repeat(201) }).valid).toBeFalse();
    });

    it('flags a code used twice, ignoring case', () => {
      const array = buildCdsReasonsArray(null);
      array.push(makeCdsReasonGroup({ code: 'Will-Monitor', display: 'a' }));
      array.push(makeCdsReasonGroup({ code: 'will-monitor', display: 'b' }));
      expect(array.getError('duplicateCode')).toBe('will-monitor');
    });

    it('reads null for an empty list and trims each reason otherwise', () => {
      const array = buildCdsReasonsArray(null);
      expect(readCdsReasons(array)).toBeNull();
      array.push(makeCdsReasonGroup({ code: ' a ', display: ' Shown ' }));
      expect(readCdsReasons(array)).toEqual([{ code: 'a', display: 'Shown' }]);
    });
  });

  describe('card links', () => {
    it('needs a label and an https address', () => {
      expect(makeCdsLinkGroup({ label: 'Guide', url: 'https://g.example.org', type: 'absolute' }).valid).toBeTrue();
      expect(makeCdsLinkGroup({ label: 'Guide', url: 'http://g.example.org', type: 'absolute' }).controls.url.hasError('httpsUrl')).toBeTrue();
      expect(makeCdsLinkGroup({ label: 'Guide', url: 'not a url', type: 'absolute' }).valid).toBeFalse();
      expect(makeCdsLinkGroup({ label: '', url: 'https://g.example.org', type: 'absolute' }).valid).toBeFalse();
    });

    it('sends app context and auto-launch for SMART links only', () => {
      const array = buildCdsLinksArray(makeService({
        links: [
          { label: 'Web', url: 'https://w.example.org', type: 'absolute', appContext: 'leftover', autolaunchable: true },
          { label: 'App', url: 'https://app.example.org/launch', type: 'smart', appContext: ' ctx ', autolaunchable: true },
          { label: 'App 2', url: 'https://app.example.org/launch', type: 'smart', appContext: '', autolaunchable: false },
        ],
      }));

      expect(readCdsLinks(array)).toEqual([
        { label: 'Web', url: 'https://w.example.org', type: 'absolute', appContext: null, autolaunchable: false },
        { label: 'App', url: 'https://app.example.org/launch', type: 'smart', appContext: 'ctx', autolaunchable: true },
        { label: 'App 2', url: 'https://app.example.org/launch', type: 'smart', appContext: null, autolaunchable: false },
      ]);
    });

    it('reads null when there are no links', () => {
      expect(readCdsLinks(buildCdsLinksArray(null))).toBeNull();
    });
  });

  describe('one-click fixes', () => {
    const REMOVE = makeDeleteFixType();
    const ALTERNATIVE = makeCreateFixType();
    const DESCRIPTORS = [REMOVE, ALTERNATIVE];

    it('is off by default, with every fix type off', () => {
      const group = buildCdsFixesGroup(DESCRIPTORS, null);
      expect(group.controls.enabled.value).toBeFalse();
      expect(group.controls.fixTypes.controls.map(t => t.controls.enabled.value)).toEqual([false, false]);
      expect(readCdsFixes(DESCRIPTORS, group)).toBeNull();
    });

    it('reads null when the check offers no fixes', () => {
      expect(readCdsFixes([], buildCdsFixesGroup([], { enabled: true, fixTypes: [] }))).toBeNull();
    });

    it('seeds from saved settings and drops a recommended fix the check no longer offers', () => {
      const group = buildCdsFixesGroup(DESCRIPTORS, {
        enabled: true,
        recommendedFixType: 'retired',
        fixTypes: [{ key: 'add-alternative', enabled: true, mappings: [{ trigger: 'nsaid', system: 's', code: 'c', display: 'd' }] }],
      });
      expect(group.controls.enabled.value).toBeTrue();
      expect(group.controls.recommendedFixType.value).toBe('');
      expect(group.controls.fixTypes.at(1).controls.mappings.length).toBe(1);
    });

    it('is invalid while on with a switched-on type that needs a mapping or has a bad row', () => {
      const group = buildCdsFixesGroup(DESCRIPTORS, null);
      group.controls.enabled.setValue(true);
      expect(cdsFixesInvalid(DESCRIPTORS, group)).withContext('on, but no fix type ticked').toBeTrue();
      expect(cdsFixesNoTypeOn(group)).toBeTrue();

      group.controls.fixTypes.at(0).controls.enabled.setValue(true);
      expect(cdsFixesNoTypeOn(group)).toBeFalse();
      expect(cdsFixesInvalid(DESCRIPTORS, group)).withContext('remove-draft needs no mapping').toBeFalse();
      group.controls.fixTypes.at(0).controls.enabled.setValue(false);

      const alt = group.controls.fixTypes.at(1);
      alt.controls.enabled.setValue(true);
      expect(cdsFixesInvalid(DESCRIPTORS, group)).withContext('no mapping yet').toBeTrue();

      alt.controls.mappings.push(makeCdsMappingGroup(ALTERNATIVE, { system: 'rx', code: '', display: 'Naproxen' }));
      expect(cdsFixesInvalid(DESCRIPTORS, group)).withContext('code missing').toBeTrue();

      alt.controls.mappings.at(0).controls.code.setValue('123');
      expect(cdsFixesInvalid(DESCRIPTORS, group)).toBeFalse();

      alt.controls.mappings.push(makeCdsMappingGroup(ALTERNATIVE, { system: 'rx', code: '9', display: 'Other' }));
      expect(alt.controls.mappings.getError('duplicateTrigger')).toBe('any');
      expect(cdsFixesInvalid(DESCRIPTORS, group)).withContext('two "any" rows').toBeTrue();
    });

    it('requires display only when the fix type says so', () => {
      expect(makeCdsMappingGroup(ALTERNATIVE, { system: 's', code: 'c', display: '' }).valid).toBeFalse();
      const optional = makeCreateFixType({ mappingFields: [{ key: 'code', label: 'Code', required: true, help: null }] });
      expect(makeCdsMappingGroup(optional, { system: 's', code: 'c', display: '' }).valid).toBeTrue();
    });

    it('defaults a new row to the first code system', () => {
      expect(makeCdsMappingGroup(ALTERNATIVE).controls.system.value)
        .toBe('urn:com.epic.cdshooks.action.code.system.preference-list-item');
    });

    it('never blocks a save while fixes are off, and saves incomplete types switched off', () => {
      const group = buildCdsFixesGroup(DESCRIPTORS, null);
      const alt = group.controls.fixTypes.at(1);
      alt.controls.enabled.setValue(true);
      alt.controls.mappings.push(makeCdsMappingGroup(ALTERNATIVE, { trigger: 'nsaid', system: 'rx', code: '1', display: 'A' }));
      alt.controls.mappings.push(makeCdsMappingGroup(ALTERNATIVE, { trigger: 'opioid', system: 'rx', code: '', display: 'B' }));

      expect(cdsFixesInvalid(DESCRIPTORS, group)).toBeFalse();
      expect(readCdsFixes(DESCRIPTORS, group)).toEqual({
        enabled: false,
        recommendedFixType: null,
        fixTypes: [
          { key: 'remove-draft', enabled: false, mappings: [] },
          { key: 'add-alternative', enabled: false, mappings: [{ trigger: 'nsaid', system: 'rx', code: '1', display: 'A' }] },
        ],
      });
    });

    it('reads a complete set of fixes, trimming values and keeping the recommended fix while its type is on', () => {
      const group = buildCdsFixesGroup(DESCRIPTORS, null);
      group.controls.enabled.setValue(true);
      group.controls.fixTypes.at(0).controls.enabled.setValue(true);
      const alt = group.controls.fixTypes.at(1);
      alt.controls.enabled.setValue(true);
      alt.controls.mappings.push(makeCdsMappingGroup(ALTERNATIVE, { trigger: '', system: ' rx ', code: ' 42 ', display: ' Naproxen ' }));
      group.controls.recommendedFixType.setValue('add-alternative');

      expect(readCdsFixes(DESCRIPTORS, group)).toEqual({
        enabled: true,
        recommendedFixType: 'add-alternative',
        fixTypes: [
          { key: 'remove-draft', enabled: true, mappings: [] },
          { key: 'add-alternative', enabled: true, mappings: [{ trigger: null, system: 'rx', code: '42', display: 'Naproxen' }] },
        ],
      });

      alt.controls.enabled.setValue(false);
      expect(readCdsFixes(DESCRIPTORS, group)?.recommendedFixType).toBeNull();
    });
  });

  describe('cdsSavedOptions', () => {
    it('sends nulls for a service that never set any option', () => {
      expect(cdsSavedOptions(makeService())).toEqual({ fetchMissingData: true, fixes: null, overrideReasons: null, links: null });
    });

    it('sends the saved options back unchanged', () => {
      const fixes = { enabled: true, fixTypes: [{ key: 'remove-draft', enabled: true, mappings: [] }], recommendedFixType: null };
      const reasons = [{ code: 'a', display: 'A' }];
      const links = [{ label: 'L', url: 'https://l.example.org', type: 'absolute' as const }];
      expect(cdsSavedOptions(makeService({
        fetchMissingData: false, fixes, fixesEnabled: true, overrideReasons: reasons, overrideReasonsAreDefault: false, links,
      }))).toEqual({ fetchMissingData: false, fixes, overrideReasons: reasons, links });
    });
  });
});
