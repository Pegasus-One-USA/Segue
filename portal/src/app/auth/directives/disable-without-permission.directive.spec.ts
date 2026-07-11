import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { DisableWithoutPermissionDirective } from './disable-without-permission.directive';
import { AuthStore } from '../store/auth.store';
import { PermissionService } from '../services/permission.service';
import { makeTestUser } from '../testing/auth-test-helpers';

@Component({
  standalone: true,
  imports: [DisableWithoutPermissionDirective],
  template: `
    <button
      class="save"
      [appDisableWithoutPermission]="'epic.edit'"
      appDisableWithoutPermissionReason="You need Epic Edit access to save changes."
    >Save</button>
  `,
})
class HostComponent {}

describe('DisableWithoutPermissionDirective', () => {
  let fixture: ComponentFixture<HostComponent>;
  let authStore: AuthStore;

  function button(): HTMLButtonElement {
    return fixture.nativeElement.querySelector('.save');
  }

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [HostComponent],
      providers: [AuthStore, PermissionService],
    });
    fixture = TestBed.createComponent(HostComponent);
    authStore = TestBed.inject(AuthStore);
  });

  it('stays in the DOM (unlike the structural directive) and is disabled when the permission is missing', () => {
    fixture.detectChanges();
    const el = button();
    expect(el).not.toBeNull();
    expect(el.disabled).toBeTrue();
    expect(el.getAttribute('aria-disabled')).toBe('true');
  });

  it('sets the reason as a title tooltip while disabled', () => {
    fixture.detectChanges();
    expect(button().getAttribute('title')).toBe('You need Epic Edit access to save changes.');
  });

  it('enables the button and clears the tooltip once the permission is held', () => {
    authStore.setUser(makeTestUser(['epic.edit']));
    fixture.detectChanges();

    const el = button();
    expect(el.disabled).toBeFalse();
    expect(el.getAttribute('aria-disabled')).toBe('false');
    expect(el.getAttribute('title')).toBeNull();
  });

  it('re-disables immediately on logout', () => {
    authStore.setUser(makeTestUser(['epic.edit']));
    fixture.detectChanges();
    expect(button().disabled).toBeFalse();

    authStore.clear();
    fixture.detectChanges();
    expect(button().disabled).toBeTrue();
  });
});
