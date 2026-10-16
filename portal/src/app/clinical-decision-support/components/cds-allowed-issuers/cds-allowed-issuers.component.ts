import { Component, input } from '@angular/core';
import { FormControl } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { CdsTrustedIssuer } from '../../models/cds.models';

/** The optional allow-list of trusted issuers that may call a service. Nothing ticked means any enabled issuer. */
@Component({
  selector: 'app-cds-allowed-issuers',
  standalone: true,
  imports: [RouterLink],
  templateUrl: './cds-allowed-issuers.component.html',
  styleUrl: './cds-allowed-issuers.component.scss',
})
export class CdsAllowedIssuersComponent {
  readonly issuers = input.required<CdsTrustedIssuer[]>();
  readonly control = input.required<FormControl<string[]>>();

  isChecked(id: string): boolean {
    return this.control().value.includes(id);
  }

  toggle(id: string, checked: boolean): void {
    const control = this.control();
    const current = control.value.filter(x => x !== id);
    control.setValue(checked ? [...current, id] : current);
    control.markAsDirty();
  }

  /** Ids on the list whose issuer no longer exists (deleted since). Shown so they can be cleared. */
  missingIds(): string[] {
    const known = new Set(this.issuers().map(i => i.id));
    return this.control().value.filter(id => !known.has(id));
  }
}
