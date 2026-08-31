import { Component, inject } from '@angular/core';
import { DialogService } from '../../services/dialog.service';
import { DialogShellComponent } from '../dialog-shell/dialog-shell.component';

/** Mounted exactly once, inside .shell-content (see app-shell.component.html) — renders one
 *  DialogShellComponent per entry in DialogService's stack. Analogous to ToastComponent/
 *  GlobalLoaderComponent: a single always-present host reading a shared signal-based service,
 *  rather than each page having to render its own dialog host. */
@Component({
  selector: 'app-dialog-outlet',
  standalone: true,
  imports: [DialogShellComponent],
  template: `
    @for (entry of dialogService.stack(); track entry.id) {
      <app-dialog-shell [entry]="entry" />
    }
  `,
})
export class DialogOutletComponent {
  protected readonly dialogService = inject(DialogService);
}
