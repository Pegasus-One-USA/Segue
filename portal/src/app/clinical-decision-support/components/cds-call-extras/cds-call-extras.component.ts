import { Component, computed, input } from '@angular/core';
import { CdsCallLog, cdsReasonCounts } from '../../models/cds.models';

interface Extra {
  label: string;
  tip: string;
}

/**
 * The extra facts of one Activity row, as small chips: what fired the Epic alert, data fetched from the EHR, fixes
 * offered or accepted, and the dismiss reason codes picked. Codes and counts only, never patient data.
 */
@Component({
  selector: 'app-cds-call-extras',
  standalone: true,
  imports: [],
  templateUrl: './cds-call-extras.component.html',
  styleUrl: './cds-call-extras.component.scss',
})
export class CdsCallExtrasComponent {
  readonly call = input.required<CdsCallLog>();

  readonly extras = computed<Extra[]>(() => {
    const c = this.call();
    const list: Extra[] = [];
    if (c.triggerAction) {
      list.push({ label: `Epic trigger ${c.triggerAction}`, tip: 'The Epic action that fired the alert (bpa-trigger-action).' });
    }
    if (c.epicVersion) list.push({ label: `Epic ${c.epicVersion}`, tip: 'Epic version that made the call.' });
    if (c.criteriaId) list.push({ label: `Criteria ${c.criteriaId}`, tip: 'The Epic criteria record that fired the alert.' });
    if (c.callbackResourceCount !== null && c.callbackResourceCount !== undefined) {
      const ms = c.callbackMs !== null && c.callbackMs !== undefined ? ` in ${c.callbackMs} ms` : '';
      list.push({
        label: `Fetched ${c.callbackResourceCount} from EHR${ms}`,
        tip: 'Data the EHR left out, read from its FHIR server for this call. Nothing was stored.',
      });
    }
    if (c.suggestionsOffered) {
      list.push({ label: `${c.suggestionsOffered} ${c.suggestionsOffered === 1 ? 'fix' : 'fixes'} offered`, tip: 'One-click fixes on the cards.' });
    }
    if (c.suggestionsAccepted) {
      list.push({ label: `${c.suggestionsAccepted} ${c.suggestionsAccepted === 1 ? 'fix' : 'fixes'} accepted`, tip: 'Fixes the clinician clicked.' });
    }
    for (const { code, count } of cdsReasonCounts(c)) {
      list.push({
        label: count > 1 ? `Dismissed: ${code} ×${count}` : `Dismissed: ${code}`,
        tip: count > 1
          ? `The dismiss reason code clinicians picked, on ${count} cards of this feedback call.`
          : 'The dismiss reason code the clinician picked.',
      });
    }
    return list;
  });
}
