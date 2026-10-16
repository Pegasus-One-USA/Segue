import { Component, input } from '@angular/core';
import { ReactiveFormsModule } from '@angular/forms';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { CDS_LINK_MAX, CdsLinkGroup, CdsLinksArray, makeCdsLinkGroup } from '../../models/cds-service-options.util';

type LinkControlName = 'label' | 'url' | 'appContext';

/**
 * Link buttons added to every card of a service: a web page, or a SMART app the EHR launches. SMART apps must be
 * on the health system's allow-list, so the Epic setup sheet lists every link.
 */
@Component({
  selector: 'app-cds-links-editor',
  standalone: true,
  imports: [ReactiveFormsModule, MatIconModule, MatTooltipModule],
  templateUrl: './cds-links-editor.component.html',
  styleUrl: './cds-links-editor.component.scss',
})
export class CdsLinksEditorComponent {
  readonly links = input.required<CdsLinksArray>();
  readonly showAllErrors = input(false);

  readonly max = CDS_LINK_MAX;

  add(): void {
    const array = this.links();
    if (array.disabled || array.length >= this.max) return;
    array.push(makeCdsLinkGroup());
    array.markAsDirty();
  }

  remove(index: number): void {
    const array = this.links();
    if (array.disabled) return;
    array.removeAt(index);
    array.markAsDirty();
  }

  showError(row: CdsLinkGroup, key: LinkControlName, error?: string): boolean {
    const control = row.controls[key];
    const visible = control.touched || this.showAllErrors();
    return visible && (error ? control.hasError(error) : control.invalid);
  }

  isSmart(row: CdsLinkGroup): boolean {
    return row.controls.type.value === 'smart';
  }
}
