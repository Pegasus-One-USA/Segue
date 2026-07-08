import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { HasPermissionDirective } from './has-permission.directive';
import { AuthStore } from '../store/auth.store';
import { PermissionService } from '../services/permission.service';
import { makeTestUser } from '../testing/auth-test-helpers';

@Component({
  standalone: true,
  imports: [HasPermissionDirective],
  template: `
    <div class="single" *appHasPermission="'epic.edit'">single</div>
    <div class="any" *appHasPermission="['epic.edit', 'epic.admin']">any</div>
    <div class="all" *appHasPermission="['epic.read', 'epic.edit']; mode: 'all'">all</div>
    <div class="none" *appHasPermission="['epic.edit']; mode: 'none'">none</div>
    <div class="withElse" *appHasPermission="'workflow.run'; else fallback">shown</div>
    <ng-template #fallback><div class="fallback">fallback shown</div></ng-template>
  `,
})
class HostComponent {}

describe('HasPermissionDirective', () => {
  let fixture: ComponentFixture<HostComponent>;
  let authStore: AuthStore;

  function query(selector: string): Element | null {
    return fixture.nativeElement.querySelector(selector);
  }

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [HostComponent],
      providers: [AuthStore, PermissionService],
    });
    fixture = TestBed.createComponent(HostComponent);
    authStore = TestBed.inject(AuthStore);
  });

  it('removes the element from the DOM (not just hides it) when the permission is missing', () => {
    fixture.detectChanges();
    expect(query('.single')).toBeNull();
  });

  it('renders the element when the single permission is held', () => {
    authStore.setUser(makeTestUser(['epic.edit']));
    fixture.detectChanges();
    expect(query('.single')).not.toBeNull();
  });

  it('"any" mode (default for a list) shows the element if at least one code matches', () => {
    authStore.setUser(makeTestUser(['epic.admin']));
    fixture.detectChanges();
    expect(query('.any')).not.toBeNull();
  });

  it('"all" mode requires every code', () => {
    authStore.setUser(makeTestUser(['epic.read']));
    fixture.detectChanges();
    expect(query('.all')).toBeNull();

    authStore.setUser(makeTestUser(['epic.read', 'epic.edit']));
    fixture.detectChanges();
    expect(query('.all')).not.toBeNull();
  });

  it('"none" mode shows the element only when the user does NOT hold the listed permission(s)', () => {
    authStore.setUser(makeTestUser([]));
    fixture.detectChanges();
    expect(query('.none')).not.toBeNull();

    authStore.setUser(makeTestUser(['epic.edit']));
    fixture.detectChanges();
    expect(query('.none')).toBeNull();
  });

  it('renders the `else` template when the permission is missing, and the primary template when it is held', () => {
    fixture.detectChanges();
    expect(query('.withElse')).toBeNull();
    expect(query('.fallback')).not.toBeNull();

    authStore.setUser(makeTestUser(['workflow.run']));
    fixture.detectChanges();
    expect(query('.withElse')).not.toBeNull();
    expect(query('.fallback')).toBeNull();
  });

  it('reacts to logout by removing previously-shown elements', () => {
    authStore.setUser(makeTestUser(['epic.edit']));
    fixture.detectChanges();
    expect(query('.single')).not.toBeNull();

    authStore.clear();
    fixture.detectChanges();
    expect(query('.single')).toBeNull();
  });
});
