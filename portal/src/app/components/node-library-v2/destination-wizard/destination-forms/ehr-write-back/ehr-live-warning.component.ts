import { ChangeDetectionStrategy, Component, input } from '@angular/core';

/** Review: a live EHR write-back cannot be taken back. Shown only for a live run into a real EHR. */
@Component({
  selector: 'app-ehr-live-warning',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrls: ['../../destination-wizard.component.scss'],
  styles: [':host { display: block; margin-top: 12px; }'],
  template: `
    <div class="dw-callout dw-callout--danger" role="alert" data-testid="ewb-live-warning">
      Live: records will be written into {{ ehr() }}. This cannot be undone.
    </div>
  `,
})
export class EhrLiveWarningComponent {
  readonly ehr = input.required<string>();
}
