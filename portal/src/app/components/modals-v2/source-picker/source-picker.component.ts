import { Component, input, output } from '@angular/core';
import { ModalOverlayComponent } from '../../shared/modal-overlay/modal-overlay.component';
import { SOURCES } from '../../../data/sources-v2.data';

@Component({
  selector: 'app-source-picker',
  standalone: true,
  imports: [ModalOverlayComponent],
  templateUrl: './source-picker.component.html',
  styleUrl: './source-picker.component.scss',
})
export class SourcePickerComponent {
  readonly open   = input(false);
  readonly closed = output<void>();
  readonly picked = output<string>(); // source id

  protected readonly sources = SOURCES;

  select(id: string): void {
    this.closed.emit();
    this.picked.emit(id);
  }
}
