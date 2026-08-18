import { SourceFormRegistry } from '../shared/config-form/config-form.contract';
import { EpicSourceFormComponent } from './epic-source-form/epic-source-form.component';
import { CernerSourceFormComponent } from './cerner-source-form/cerner-source-form.component';
import { AthenahealthSourceFormComponent } from './athenahealth-source-form/athenahealth-source-form.component';
import { AllscriptsSourceFormComponent } from './allscripts-source-form/allscripts-source-form.component';
import { HealowSourceFormComponent } from './healow-source-form/healow-source-form.component';
import { MeditechSourceFormComponent } from './meditech-source-form/meditech-source-form.component';
import { GenericFhirSourceFormComponent } from './generic-fhir-source-form/generic-fhir-source-form.component';
import { Hl7v2SourceFormComponent } from './hl7v2-source-form/hl7v2-source-form.component';
import { SampleSourceFormComponent } from './sample-source-form/sample-source-form.component';

/**
 * Dispatches a SOURCES catalog `id` (see ../../data/sources.data.ts) to the standalone component that configures
 * it. Replaces the `if (item.id === 'epic') {...} else if (item.id === 'generic-fhir') {...}` special-casing that
 * used to live in NodeLibraryDialogComponent.selectItem() — adding a new source type is now "register one more
 * entry here", never a switch/if-chain edit (see FHIRBridge's registry-over-switch architecture rule).
 */
export const SOURCE_FORM_REGISTRY: SourceFormRegistry = {
  epic: EpicSourceFormComponent,
  cerner: CernerSourceFormComponent,
  athena: AthenahealthSourceFormComponent,
  allscripts: AllscriptsSourceFormComponent,
  healow: HealowSourceFormComponent,
  meditech: MeditechSourceFormComponent,
  'generic-fhir': GenericFhirSourceFormComponent,
  hl7v2: Hl7v2SourceFormComponent,
  sample: SampleSourceFormComponent,
};

/** Maps the backend's EhrVendor (SourceSystemType) enum member names back to the SOURCE_FORM_REGISTRY keys above —
 *  used wherever a vendor value (rather than a sources.data.ts `id`) is the only thing known, e.g.
 *  SourceConnectionListComponent's entity-mode rows, which only ever carry `sourceSystemType`. */
export const EHR_VENDOR_TO_SOURCE_FORM_KEY: Record<string, string> = {
  Epic: 'epic',
  Cerner: 'cerner',
  Athenahealth: 'athena',
  Allscripts: 'allscripts',
  Healow: 'healow',
  MeditechGreenfield: 'meditech',
  GenericFhir: 'generic-fhir',
  Hl7v2: 'hl7v2',
  Sample: 'sample',
};

/** Registry keys backed by the shared EhrVendorSourceFormComponent engine — each owns its full save/cancel flow
 *  end-to-end via WizardService (audience config, discovery, JWT key management, existing-connection cloning, ...)
 *  and its own topbar/footer chrome, exactly like the old (Epic-only) EpicAudienceFormComponent did. Everything
 *  else in SOURCE_FORM_REGISTRY (currently generic-fhir, hl7v2) is "headless": it only implements
 *  SourceConfigFormComponent.getFields() and has no WizardService/entity-mode integration at all. Shared by
 *  NodeLibraryDialogComponent (canvas mode) and SourceConnectionListComponent (entity mode). */
export const SELF_CONTAINED_SOURCE_FORM_KEYS = new Set(['epic', 'cerner', 'athena', 'allscripts', 'healow', 'meditech', 'sample']);
