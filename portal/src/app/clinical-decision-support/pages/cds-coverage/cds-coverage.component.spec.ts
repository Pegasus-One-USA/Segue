import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { HttpErrorResponse } from '@angular/common/http';
import { of, throwError } from 'rxjs';
import { CdsCoverageComponent } from './cds-coverage.component';
import { AuthStore } from '../../../auth/store/auth.store';
import { DialogService } from '../../../core/services/dialog.service';
import { ToastService } from '../../../services/toast.service';
import { CdsApiService } from '../../services/cds-api.service';
import { CdsCoverageApiService } from '../../services/cds-coverage-api.service';
import {
  CDS_ALL_PERMISSIONS, makeCdsApiSpy, makeCoverageApiSpy, makeDialogSpy, makePayer, makeService, makeToastSpy, signInWith,
} from '../../testing/cds-test-data';

describe('CdsCoverageComponent', () => {
  let coverageApi: ReturnType<typeof makeCoverageApiSpy>;
  let cdsApi: ReturnType<typeof makeCdsApiSpy>;
  let toast: ReturnType<typeof makeToastSpy>;

  function create(setup: (c: typeof coverageApi, a: typeof cdsApi) => void = () => undefined) {
    coverageApi = makeCoverageApiSpy();
    cdsApi = makeCdsApiSpy();
    toast = makeToastSpy();
    setup(coverageApi, cdsApi);
    TestBed.configureTestingModule({
      imports: [CdsCoverageComponent],
      providers: [
        provideRouter([]),
        provideNoopAnimations(),
        { provide: CdsCoverageApiService, useValue: coverageApi },
        { provide: CdsApiService, useValue: cdsApi },
        { provide: ToastService, useValue: toast },
        { provide: DialogService, useValue: makeDialogSpy() },
      ],
    });
    signInWith(TestBed.inject(AuthStore), CDS_ALL_PERMISSIONS);
    const fixture = TestBed.createComponent(CdsCoverageComponent);
    fixture.detectChanges();
    return { fixture, page: fixture.componentInstance, el: fixture.nativeElement as HTMLElement };
  }

  it('shows the payers (sorted) and the rules', () => {
    const { el } = create(c => c.getPayers.and.returnValue(of([makePayer({ id: 'z', name: 'Zeta' }), makePayer()])));
    const rows = el.querySelectorAll('[data-testid="cds-payer-row"]');
    expect(rows[0].textContent).toContain('Acme Health Plan');
    expect(rows[1].textContent).toContain('Zeta');
    expect(el.querySelector('[data-testid="cds-rule-list"]')).not.toBeNull();
    expect(coverageApi.getRules).toHaveBeenCalled();
  });

  it('points to Services when no coverage requirements service exists', () => {
    const { el } = create();
    expect(el.querySelector('[data-testid="cds-coverage-no-service"]')?.textContent).toContain('Coverage requirements (CRD)');
  });

  it('hides that hint once a coverage requirements service exists, or when services cannot be read', () => {
    expect(create((_, a) => a.getServices.and.returnValue(of([makeService({ handlerKey: 'coverage-requirements' })])))
      .el.querySelector('[data-testid="cds-coverage-no-service"]')).toBeNull();
    TestBed.resetTestingModule();
    expect(create((_, a) => a.getServices.and.returnValue(throwError(() => new HttpErrorResponse({ status: 500 }))))
      .el.querySelector('[data-testid="cds-coverage-no-service"]')).toBeNull();
  });

  it('reloads payers and rules after a payer change', () => {
    const { page } = create();
    coverageApi.getPayers.calls.reset();
    coverageApi.getRules.calls.reset();
    page.onPayersChanged();
    expect(coverageApi.getPayers).toHaveBeenCalledTimes(1);
    expect(coverageApi.getRules).toHaveBeenCalledTimes(1);
  });

  it('says so when the payers cannot be loaded', () => {
    create(c => c.getPayers.and.returnValue(throwError(() => new HttpErrorResponse({ status: 500 }))));
    expect(toast.error).toHaveBeenCalledWith('Could not load the payers.');
  });
});
