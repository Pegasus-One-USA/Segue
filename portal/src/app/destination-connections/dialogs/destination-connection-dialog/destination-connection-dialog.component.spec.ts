import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { DestinationConnectionDialogComponent, DestinationConnectionDialogData } from './destination-connection-dialog.component';
import { DIALOG_DATA, DialogRef } from '../../../core/services/dialog.service';
import { PermissionService } from '../../../auth/services/permission.service';
import { PermissionActionGuard } from '../../../auth/services/permission-action-guard.service';

/** The Destination Connections "New" picker already chose the type, so the dialog skips its own type grid. */
describe('DestinationConnectionDialogComponent', () => {
  function create(data: DestinationConnectionDialogData) {
    TestBed.configureTestingModule({
      imports: [DestinationConnectionDialogComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideNoopAnimations(),
        { provide: DIALOG_DATA, useValue: data },
        { provide: DialogRef, useValue: { close: () => undefined } },
        { provide: PermissionService, useValue: { hasPermission: () => true, hasAll: () => true, hasAny: () => true } },
        { provide: PermissionActionGuard, useValue: { ensure: () => true } },
      ],
    });
    return TestBed.createComponent(DestinationConnectionDialogComponent).componentInstance;
  }

  it('starts on the given type', () => {
    const dialog = create({ mode: 'create', initialType: 'Mongo' });

    expect(dialog.chosenType()).toBe('Mongo');
  });

  it('starts on the type grid without one', () => {
    const dialog = create({ mode: 'create' });

    expect(dialog.chosenType()).toBeNull();
    expect(dialog.createTypeCards.length).toBeGreaterThan(0);
  });
});
