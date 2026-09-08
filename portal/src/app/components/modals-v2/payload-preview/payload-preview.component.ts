import { Component, input, output, inject } from '@angular/core';
import { ModalOverlayComponent } from '../../shared/modal-overlay/modal-overlay.component';
import { PayloadServiceV2 } from '../../../services/payload-v2.service';
import { ToastService } from '../../../services/toast.service';

@Component({
  selector: 'app-payload-preview',
  standalone: true,
  imports: [ModalOverlayComponent],
  templateUrl: './payload-preview.component.html',
  styleUrl: './payload-preview.component.scss',
})
export class PayloadPreviewComponent {
  private readonly payloadSvc = inject(PayloadServiceV2);
  private readonly toast      = inject(ToastService);

  readonly open        = input(false);
  readonly scenarioName = input('');
  readonly closed      = output<void>();

  protected get json(): string {
    return JSON.stringify(this.payloadSvc.build(this.scenarioName()), null, 2);
  }

  async copy(): Promise<void> {
    try {
      await navigator.clipboard.writeText(this.json);
      this.toast.show('Copied', 'Payload copied to clipboard.');
    } catch {
      this.toast.show('Copy failed', 'Select the text manually.');
    }
  }
}
