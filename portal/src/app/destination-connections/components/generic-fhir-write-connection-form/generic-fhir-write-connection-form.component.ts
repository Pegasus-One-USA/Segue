import { Component, OnInit, inject, input, output, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import { ISourceConnectionService } from '../../../source-connections/services/i-source-connection.service';
import { SourceConnectionModel, SourceConnectionRequest } from '../../../source-connections/models/source-connection.model';
import { ToastService } from '../../../services/toast.service';

/**
 * A plain FHIR server an EHR Write-Back destination writes to (as plain FHIR, or as a test server that receives
 * exactly what Epic, eClinicalWorks or athenahealth would). Saved as a GenericFhir SourceConnection with Backend
 * application type, no authentication and, when new, write-only access. There is no vendor form for this, so the
 * EHR write connections panel opens this instead.
 */
@Component({
  selector: 'app-generic-fhir-write-connection-form',
  standalone: true,
  imports: [ReactiveFormsModule],
  templateUrl: './generic-fhir-write-connection-form.component.html',
  styleUrl: './generic-fhir-write-connection-form.component.scss',
})
export class GenericFhirWriteConnectionFormComponent implements OnInit {
  private readonly svc = inject(ISourceConnectionService);
  private readonly toast = inject(ToastService);
  private readonly fb = inject(FormBuilder);

  /** The connection to view or edit; null creates a new one. */
  readonly existing = input<SourceConnectionModel | null>(null);
  readonly readonly = input(false);

  readonly saved = output<void>();
  readonly cancelled = output<void>();

  readonly saving = signal(false);
  readonly error = signal<string | null>(null);

  readonly form = this.fb.nonNullable.group({
    name: ['FHIR server', [Validators.required, Validators.maxLength(200)]],
    baseUrl: ['', [Validators.required, Validators.pattern(/^https?:\/\/\S+$/i)]],
  });

  ngOnInit(): void {
    const c = this.existing();
    if (c) this.form.setValue({ name: c.name, baseUrl: c.baseUrl });
    if (this.readonly()) this.form.disable();
  }

  save(): void {
    this.form.markAllAsTouched();
    if (this.form.invalid || this.saving()) return;

    const c = this.existing();
    const v = this.form.getRawValue();
    const request: SourceConnectionRequest = {
      name: v.name.trim(),
      sourceSystemType: 'GenericFhir',
      baseUrl: v.baseUrl.trim(),
      // An edit changes only the name and base URL. A Read & Write row may also feed scheduled source runs, which
      // read its saved authentication, application type and retrieval, so those round-trip unchanged.
      authentication: c ? { ...c.authentication, inlineClientSecret: null } : { authenticationType: 'None', scopes: [] },
      applicationType: c ? (c.applicationType ?? null) : 'Backend',
      // New: write-only. Edit: null keeps whatever access is saved (a Read & Write row stays one).
      access: c ? null : 'Write',
      interactive: c?.interactive ?? null,
      retrieval: c?.retrieval ?? null,
    };

    this.saving.set(true);
    this.error.set(null);
    const call = c ? this.svc.update(c.id, request) : this.svc.create(request);
    call.subscribe({
      next: () => {
        this.saving.set(false);
        this.toast.success(c ? 'EHR write connection updated.' : 'EHR write connection created.');
        this.saved.emit();
      },
      error: (err: HttpErrorResponse) => {
        this.saving.set(false);
        // ConfigurationService validation failures come back as { error: "<message>" }.
        const msg = err?.error?.error ?? err?.error?.title ?? 'Saving the connection failed. Try again.';
        this.error.set(typeof msg === 'string' ? msg : 'Saving the connection failed. Try again.');
      },
    });
  }
}
