import { Component, input, output } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
import { CDS_SANDBOX } from '../../models/cds-sandbox';
import { CdsCopyButtonComponent } from '../cds-copy-button/cds-copy-button.component';

/** How to try a service with the public CDS Hooks sandbox, with its issuer values ready to copy or add. */
@Component({
  selector: 'app-cds-sandbox-hint',
  standalone: true,
  imports: [MatIconModule, CdsCopyButtonComponent],
  templateUrl: './cds-sandbox-hint.component.html',
  styleUrl: './cds-sandbox-hint.component.scss',
})
export class CdsSandboxHintComponent {
  /** The discovery URL to paste into the sandbox. */
  readonly discoveryUrl = input<string | null>(null);
  /** Shows the "Add as trusted issuer" button (needs cdshooks.create, checked by the page). */
  readonly canAdd = input(false);
  /** True once an issuer with the sandbox's iss exists. */
  readonly alreadyAdded = input(false);

  readonly addSandbox = output<void>();

  readonly sandbox = CDS_SANDBOX;
}
