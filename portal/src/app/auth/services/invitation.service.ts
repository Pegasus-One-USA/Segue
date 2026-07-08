import { Injectable, inject, signal } from '@angular/core';
import { Observable, of, throwError } from 'rxjs';
import { delay, switchMap } from 'rxjs/operators';
import { Invitation, InvitationStatus, InvitationValidationResult, SendInvitationRequest } from '../models/invitation.model';
import { PasswordPolicyService } from './password-policy.service';
import { EmailNotificationService } from './email-notification.service';
import { AccountSecurityService } from './account-security.service';
import { MOCK_USERS, ALL_ROLES } from '../mock/mock-db';
import { User } from '../models/user.model';
import { MessageResponse } from '../models/user.model';

const ORG_NAME         = 'FHIRBridge Healthcare';
const TOKEN_EXPIRY_MS  = 24 * 60 * 60 * 1000; // 24 hours
const PASSWORD_HISTORY = 20;
const STORAGE_KEY      = 'fhirbridge_invitations';

@Injectable({ providedIn: 'root' })
export class InvitationService {

  private readonly policysSvc  = inject(PasswordPolicyService);
  private readonly emailSvc    = inject(EmailNotificationService);
  private readonly securitySvc = inject(AccountSecurityService);

  private readonly _invitations    = signal<Invitation[]>(this.loadFromStorage());
  private readonly passwordHistory = new Map<string, string[]>(); // userId -> last 20 passwords

  readonly invitations = this._invitations.asReadonly();

  // ── localStorage persistence ───────────────────────────────────────────────

  private loadFromStorage(): Invitation[] {
    try {
      const raw = localStorage.getItem(STORAGE_KEY);
      if (!raw) return [];
      const parsed = JSON.parse(raw) as Record<string, unknown>[];
      return parsed.map(i => ({
        ...i,
        createdAt: new Date(i['createdAt'] as string),
        expiresAt: new Date(i['expiresAt'] as string),
        acceptedAt: i['acceptedAt'] ? new Date(i['acceptedAt'] as string) : undefined,
      })) as Invitation[];
    } catch {
      return [];
    }
  }

  private saveToStorage(invitations: Invitation[]): void {
    try {
      localStorage.setItem(STORAGE_KEY, JSON.stringify(invitations));
    } catch { /* quota exceeded — ignore */ }
  }

  // ── Send invitation ────────────────────────────────────────────────────────

  sendInvitation(req: SendInvitationRequest): Observable<Invitation> {
    return of(null).pipe(
      delay(900),
      switchMap(() => {
        const exists = MOCK_USERS.find(u => u.email.toLowerCase() === req.email.toLowerCase());
        if (exists) {
          return throwError(() => ({
            code:    'EMAIL_TAKEN',
            message: 'A user with this email address already exists.',
          }));
        }

        const token     = this.generateToken();
        const now       = new Date();
        const expiresAt = new Date(now.getTime() + TOKEN_EXPIRY_MS);

        // Create the pending user in mock DB
        const roleObj   = ALL_ROLES.find(r => r.name === req.role) ?? ALL_ROLES[ALL_ROLES.length - 1];
        const newUser: User = {
          id:                 `u-${Date.now()}`,
          email:              req.email.trim().toLowerCase(),
          firstName:          req.firstName.trim(),
          lastName:           req.lastName.trim(),
          fullName:           `${req.firstName.trim()} ${req.lastName.trim()}`,
          role:               roleObj.name,
          roles:              [roleObj],
          permissions:        roleObj.permissions,
          orgId:              'org-001',
          status:             'pending',
          loginType:          'local',
          mustChangePassword: true,
          emailVerified:      false,
          twoFactorEnabled:   false,
          invitedBy:          req.invitedBy,
          createdAt:          now.toISOString(),
          updatedAt:          now.toISOString(),
          passwordHash:       undefined,
        };
        MOCK_USERS.push(newUser);

        const invitation: Invitation = {
          id:               `inv-${Date.now()}`,
          token,
          email:            req.email.trim().toLowerCase(),
          firstName:        req.firstName.trim(),
          lastName:         req.lastName.trim(),
          role:             roleObj.name,
          organizationName: ORG_NAME,
          invitedBy:        req.invitedBy,
          createdAt:        now,
          expiresAt,
          status:           'pending',
        };

        this._invitations.update(list => {
          const updated = [...list, invitation];
          this.saveToStorage(updated);
          return updated;
        });

        // "Send" the email
        const activationUrl = `${window.location.origin}/auth/set-password?token=${token}`;
        this.emailSvc.sendInvitationEmail(
          invitation.email,
          invitation.firstName,
          ORG_NAME,
          activationUrl,
          expiresAt,
        ).subscribe();

        this.securitySvc.logEvent('invitation_sent', invitation.email, undefined,
          `Invited by ${req.invitedBy}`);

        return of(invitation);
      })
    );
  }

  // ── Validate token ─────────────────────────────────────────────────────────

  validateToken(token: string): Observable<InvitationValidationResult> {
    return of(null).pipe(
      delay(600),
      switchMap(() => {
        const inv = this._invitations().find(i => i.token === token);

        if (!inv) {
          return of<InvitationValidationResult>({ valid: false, error: 'invalid' });
        }
        if (inv.status === 'accepted') {
          return of<InvitationValidationResult>({ valid: false, error: 'already_accepted' });
        }
        if (inv.status === 'cancelled') {
          return of<InvitationValidationResult>({ valid: false, error: 'cancelled' });
        }
        if (new Date() > inv.expiresAt) {
          this.updateInvitationStatus(token, 'expired');
          return of<InvitationValidationResult>({ valid: false, error: 'expired' });
        }

        return of<InvitationValidationResult>({ valid: true, invitation: inv });
      })
    );
  }

  // ── Accept invitation (set password + activate) ────────────────────────────

  acceptInvitation(token: string, password: string): Observable<MessageResponse> {
    return of(null).pipe(
      delay(1000),
      switchMap(() => {
        const inv = this._invitations().find(i => i.token === token);

        if (!inv) {
          return throwError(() => ({ code: 'INVALID_TOKEN', message: 'Invalid invitation token.' }));
        }
        if (inv.status !== 'pending') {
          return throwError(() => ({ code: 'TOKEN_USED', message: 'This invitation has already been used.' }));
        }
        if (new Date() > inv.expiresAt) {
          this.updateInvitationStatus(token, 'expired');
          return throwError(() => ({ code: 'TOKEN_EXPIRED', message: 'This invitation link has expired.' }));
        }

        // Validate password meets policy
        const validation = this.policysSvc.validate(password);
        if (!validation.allMet) {
          return throwError(() => ({ code: 'WEAK_PASSWORD', message: 'Password does not meet security requirements.' }));
        }

        // Find the user
        const user = MOCK_USERS.find(u => u.email === inv.email);
        if (!user) {
          return throwError(() => ({ code: 'USER_NOT_FOUND', message: 'Associated user account not found.' }));
        }

        // Check password history (mock: last 20)
        const history = this.passwordHistory.get(user.id) ?? [];
        if (history.includes(password)) {
          return throwError(() => ({ code: 'PASSWORD_REUSED', message: 'You cannot reuse a recent password.' }));
        }

        // Activate the user
        user.passwordHash       = password;
        user.status             = 'active';
        user.mustChangePassword = false;
        user.emailVerified      = true;
        user.updatedAt          = new Date().toISOString();

        // Update password history
        const newHistory = [password, ...history].slice(0, PASSWORD_HISTORY);
        this.passwordHistory.set(user.id, newHistory);

        // Mark invitation accepted
        this._invitations.update(list => {
          const updated = list.map(i => i.token === token
            ? { ...i, status: 'accepted' as InvitationStatus, acceptedAt: new Date() }
            : i
          );
          this.saveToStorage(updated);
          return updated;
        });

        // Notify
        this.emailSvc.sendInvitationAcceptedEmail(inv.email, inv.firstName).subscribe();
        this.securitySvc.logEvent('invitation_accepted', inv.email, user.id, 'Account activated via invitation');

        return of<MessageResponse>({ success: true, message: 'Account activated successfully.' });
      })
    );
  }

  // ── Resend invitation ──────────────────────────────────────────────────────

  resendInvitation(email: string, invitedBy: string): Observable<Invitation> {
    return of(null).pipe(
      delay(700),
      switchMap(() => {
        const existing = this._invitations().find(
          i => i.email === email.toLowerCase() && i.status === 'pending'
        );

        // Cancel existing pending invitation
        if (existing) {
          this.updateInvitationStatus(existing.token, 'cancelled');
        }

        const user = MOCK_USERS.find(u => u.email === email.toLowerCase());
        if (!user) {
          return throwError(() => ({ code: 'USER_NOT_FOUND', message: 'User not found.' }));
        }

        return this.sendInvitation({
          email,
          firstName: user.firstName,
          lastName:  user.lastName,
          role:      user.role,
          invitedBy,
        });
      })
    );
  }

  // ── Helpers ────────────────────────────────────────────────────────────────

  getActivationUrl(token: string): string {
    return `${window.location.origin}/auth/set-password?token=${token}`;
  }

  private generateToken(): string {
    const rand = Array.from(crypto.getRandomValues(new Uint8Array(24)))
      .map(b => b.toString(16).padStart(2, '0'))
      .join('');
    return `inv_${Date.now()}_${rand}`;
  }

  private updateInvitationStatus(token: string, status: InvitationStatus): void {
    this._invitations.update(list => {
      const updated = list.map(i => i.token === token ? { ...i, status } : i);
      this.saveToStorage(updated);
      return updated;
    });
  }
}
