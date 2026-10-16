import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { CdsShellComponent } from './cds-shell.component';
import { AuthStore } from '../../../auth/store/auth.store';
import { makeTestUser } from '../../../auth/testing/auth-test-helpers';
import { User } from '../../../auth/models/user.model';

/** The tab strip uses the same permission rules as the child routes, so a tab never leads to /unauthorized. */
describe('CdsShellComponent', () => {
  function labels(user: User): string[] {
    TestBed.configureTestingModule({ imports: [CdsShellComponent], providers: [provideRouter([])] });
    TestBed.inject(AuthStore).setUser(user);
    const fixture = TestBed.createComponent(CdsShellComponent);
    fixture.detectChanges();
    return Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('.shell-tab')).map(a => a.textContent?.trim() ?? '')
      .map(t => t.replace(/^\S+\s+/, ''));
  }

  it('shows every tab but Test to a view-only role', () => {
    expect(labels(makeTestUser(['cdshooks.view']))).toEqual(['Services', 'Connect your EHR', 'Activity']);
  });

  it('shows Test only with both view and test', () => {
    expect(labels(makeTestUser(['cdshooks.view', 'cdshooks.test']))).toContain('Test');
  });

  it('hides Test for test without view', () => {
    expect(labels(makeTestUser(['cdshooks.test']))).toEqual([]);
  });

  it('shows every tab to an admin', () => {
    expect(labels(makeTestUser([], 'Admin'))).toEqual(['Services', 'Connect your EHR', 'Test', 'Activity']);
  });
});
