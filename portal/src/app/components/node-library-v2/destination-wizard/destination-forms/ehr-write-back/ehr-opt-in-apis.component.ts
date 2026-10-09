import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { EhrMissingOptIn, OptInApi } from './ehr-write-back.model';

/** The vendor APIs a destination must turn on before a selected type is sent through them, one per variant. One a
 *  selected type still needs is marked, so Next is never blocked without saying why. */
@Component({
  selector: 'app-ehr-opt-in-apis',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrls: ['../../destination-wizard.component.scss'],
  styles: [':host { display: block; }'],
  template: `
    <div class="dw-field dw-field--full">
      <span class="dw-label">Also write through these {{ vendorLabel() }} APIs</span>
      @for (api of apis(); track api.variant) {
        <label class="dw-label" [attr.data-variant]="api.variant">
          <input type="checkbox" [checked]="enabled().includes(api.variant)" (change)="toggled.emit(api.variant)" />
          {{ api.label }}
          @if (api.tabularOnly && !sourceIsTabular()) {
            <span class="dw-hint">(needs a CSV / SQL source)</span>
          }
          @if (neededBy(api.variant); as types) {
            <span class="dw-hint dw-hint--warn">Needed to write {{ types }}</span>
          }
        </label>
      }
      <span class="dw-hint">Selecting the resource type is not enough for these: each is sent only when ticked
        here. Those needing a CSV / SQL source need the target EHR's own ids (a questionnaire, a referral, an imaging
        report), so records from another EHR are skipped.</span>
    </div>
  `,
})
export class EhrOptInApisComponent {
  readonly apis = input<OptInApi[]>([]);
  readonly enabled = input<readonly string[]>([]);
  readonly vendorLabel = input<string>('');
  readonly missing = input<EhrMissingOptIn[]>([]);
  readonly sourceIsTabular = input<boolean>(false);

  readonly toggled = output<string>();

  private readonly typesByVariant = computed(() => {
    const byVariant = new Map<string, string[]>();
    for (const m of this.missing()) {
      for (const variant of m.variants) byVariant.set(variant, [...(byVariant.get(variant) ?? []), m.resourceType]);
    }
    return byVariant;
  });

  /** The selected types still waiting on this API, as text; '' when none. */
  neededBy(variant: string): string {
    return (this.typesByVariant().get(variant) ?? []).join(', ');
  }
}
