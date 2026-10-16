import { Component, inject, input, signal } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { ToastService } from '../../../services/toast.service';

/** Small icon button that copies a value (a URL, an issuer) to the clipboard. */
@Component({
  selector: 'app-cds-copy-button',
  standalone: true,
  imports: [MatIconModule, MatTooltipModule],
  templateUrl: './cds-copy-button.component.html',
  styleUrl: './cds-copy-button.component.scss',
})
export class CdsCopyButtonComponent {
  private readonly toast = inject(ToastService);

  readonly value = input.required<string>();
  /** What is being copied, for the tooltip and screen readers (e.g. "discovery URL"). */
  readonly what = input('value');

  readonly copied = signal(false);

  copy(): void {
    navigator.clipboard.writeText(this.value()).then(
      () => {
        this.copied.set(true);
        setTimeout(() => this.copied.set(false), 1500);
      },
      () => this.toast.error('Could not copy to the clipboard.'),
    );
  }
}
