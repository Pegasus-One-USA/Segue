import { TestBed } from '@angular/core/testing';
import { CdsFixesConfirmService } from './cds-fixes-confirm.service';
import { DialogService } from '../../core/services/dialog.service';
import { ConfirmDialogComponent } from '../../core/components/confirm-dialog/confirm-dialog.component';
import { makeDialogSpy } from '../testing/cds-test-data';

describe('CdsFixesConfirmService', () => {
  function setup(answer: unknown) {
    const dialog = makeDialogSpy(answer);
    TestBed.configureTestingModule({ providers: [{ provide: DialogService, useValue: dialog }] });
    return { service: TestBed.inject(CdsFixesConfirmService), dialog };
  }

  function result(service: CdsFixesConfirmService, was: boolean, will: boolean): boolean | undefined {
    let value: boolean | undefined;
    service.confirmSave(was, will).subscribe(v => (value = v));
    return value;
  }

  it('goes ahead without asking unless the save turns fixes on', () => {
    const { service, dialog } = setup(false);
    expect(result(service, false, false)).toBeTrue();
    expect(result(service, true, true)).toBeTrue();
    expect(result(service, true, false)).toBeTrue();
    expect(dialog.open).not.toHaveBeenCalled();
  });

  it('asks a danger question when fixes go from off to on, and goes ahead on Save', () => {
    const { service, dialog } = setup(true);
    expect(result(service, false, true)).toBeTrue();
    expect(dialog.open).toHaveBeenCalledWith(ConfirmDialogComponent, jasmine.objectContaining({
      data: jasmine.objectContaining({ title: 'Let this service change EHR drafts?', confirmLabel: 'Save', danger: true }),
    }));
  });

  it('stops when the question is cancelled or closed', () => {
    expect(result(setup(false).service, false, true)).toBeFalse();
    TestBed.resetTestingModule();
    expect(result(setup(undefined).service, false, true)).toBeFalse();
  });
});
