import { Component, OnDestroy, computed, effect, inject, output, signal, untracked } from '@angular/core';
import { EhrVendor } from '../../../ehr-endpoints/models/ehr-endpoint.model';
import { WizardService } from '../../../services/wizard.service';
import { SourceConnectionModel } from '../../../source-connections/models/source-connection.model';
import { EpicSourceFormComponent } from '../../../components/node-library/epic-source-form/epic-source-form.component';
import { HealowSourceFormComponent } from '../../../components/node-library/healow-source-form/healow-source-form.component';
import { AthenahealthSourceFormComponent } from '../../../components/node-library/athenahealth-source-form/athenahealth-source-form.component';
import { GenericFhirWriteConnectionFormComponent } from '../generic-fhir-write-connection-form/generic-fhir-write-connection-form.component';

/**
 * Opens an EHR write connection's form — new, view or edit. Epic / eClinicalWorks / athenahealth open the same vendor
 * form Source Connections uses, in write purpose (Backend System only, Access = Write); a plain FHIR server has its
 * own small form. Permission checks belong to the caller. Used by the Destination Connections list and reusable
 * wherever a write connection is picked or created on the go.
 */
@Component({
  selector: 'app-ehr-write-connection-editor',
  standalone: true,
  imports: [
    EpicSourceFormComponent,
    HealowSourceFormComponent,
    AthenahealthSourceFormComponent,
    GenericFhirWriteConnectionFormComponent,
  ],
  templateUrl: './ehr-write-connection-editor.component.html',
  styleUrl: './ehr-write-connection-editor.component.scss',
})
export class EhrWriteConnectionEditorComponent implements OnDestroy {
  protected readonly wiz = inject(WizardService);

  /** A connection was saved: the saved connection as the API returned it (null if a vendor save returned none). */
  readonly saved = output<SourceConnectionModel | null>();
  /** The open form closed (saved or not). */
  readonly closed = output<void>();

  readonly formMaximized = signal(false);
  /** The plain FHIR server form: 'new', the connection being viewed / edited, or null when closed. */
  readonly genericFhirEditing = signal<SourceConnectionModel | 'new' | null>(null);
  readonly genericFhirReadonly = signal(false);
  readonly genericFhirExisting = computed(() => {
    const editing = this.genericFhirEditing();
    return editing === 'new' ? null : editing;
  });

  /** The vendor form modal is this editor's only while a write-purpose entity session is open (WizardService is a
   *  root singleton shared with Source Connections). */
  readonly vendorFormOpen = computed(
    () => this.wiz.isOpen() && this.wiz.wizardMode() === 'entity' && this.wiz.purpose() === 'write',
  );

  /** Baseline so `saved` fires only on a save made after this editor was created. */
  private readonly savedBaseline = this.wiz.saved();
  private vendorFormWasOpen = false;

  constructor() {
    effect(() => {
      if (this.wiz.saved() !== this.savedBaseline) this.saved.emit(untracked(() => this.wiz.lastSavedEntity()));
    });
    effect(() => {
      if (!this.wiz.isOpen()) this.formMaximized.set(false);
    });
    effect(() => {
      const open = this.vendorFormOpen();
      if (this.vendorFormWasOpen && !open) this.closed.emit();
      this.vendorFormWasOpen = open;
    });
  }

  /** WizardService outlives this editor: leaving mid-edit must not leave a write session open. */
  ngOnDestroy(): void {
    if (this.vendorFormOpen()) this.wiz.close();
  }

  openNew(vendor: EhrVendor): void {
    if (vendor === 'GenericFhir') {
      this.genericFhirReadonly.set(false);
      this.genericFhirEditing.set('new');
      return;
    }
    // openEntity(null) seeds ehrType to Epic; set the chosen vendor right after, as Source Connections does.
    this.wiz.openEntity(null, { purpose: 'write' });
    this.wiz.ehrType.set(vendor);
  }

  open(c: SourceConnectionModel, readonly: boolean): void {
    if (c.sourceSystemType === 'GenericFhir') {
      this.genericFhirReadonly.set(readonly);
      this.genericFhirEditing.set(c);
      return;
    }
    this.wiz.openEntity(c, { readonly, purpose: 'write' });
  }

  onVendorBackdropClick(event: MouseEvent): void {
    if (event.target === event.currentTarget) this.wiz.close();
  }

  closeGenericFhir(): void {
    this.genericFhirEditing.set(null);
    this.closed.emit();
  }

  onGenericFhirSaved(model: SourceConnectionModel): void {
    this.genericFhirEditing.set(null);
    this.saved.emit(model);
    this.closed.emit();
  }

  onGenericFhirBackdropClick(event: MouseEvent): void {
    if (event.target === event.currentTarget) this.closeGenericFhir();
  }
}
