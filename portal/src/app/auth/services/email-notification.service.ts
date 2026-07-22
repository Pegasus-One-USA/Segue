import { Injectable, signal } from '@angular/core';
import { Observable, of } from 'rxjs';
import { delay } from 'rxjs/operators';

export interface MockEmail {
  id:        string;
  to:        string;
  subject:   string;
  body:      string;
  sentAt:    Date;
  type:      string;
}

@Injectable({ providedIn: 'root' })
export class EmailNotificationService {

  private readonly _sentEmails = signal<MockEmail[]>([]);
  readonly sentEmails = this._sentEmails.asReadonly();

  sendInvitationEmail(
    to: string,
    firstName: string,
    orgName: string,
    activationUrl: string,
    expiresAt: Date,
  ): Observable<void> {
    const email: MockEmail = {
      id:      `email-${Date.now()}`,
      to,
      subject: `You're invited to join ${orgName} on Segue`,
      type:    'invitation',
      sentAt:  new Date(),
      body: `
Dear ${firstName},

You have been invited to join ${orgName} on Segue — the Healthcare Integration Platform.

To activate your account, click the button below:
  ${activationUrl}

This invitation expires on ${expiresAt.toLocaleString()}.

If you did not expect this invitation, you may safely ignore this email.

— The Segue Team
      `.trim(),
    };

    this._sentEmails.update(list => [...list, email]);
    console.info('[EmailService] Invitation email sent to', to, '\nActivation URL:', activationUrl);
    return of(undefined).pipe(delay(200));
  }

  sendPasswordChangedEmail(to: string, firstName: string): Observable<void> {
    this.log(to, 'password_changed', `Password changed`, `Dear ${firstName}, your Segue password was just changed.`);
    return of(undefined).pipe(delay(100));
  }

  sendPasswordResetEmail(to: string, firstName: string, resetUrl: string): Observable<void> {
    this.log(to, 'password_reset', `Reset your Segue password`, `Dear ${firstName}, use this link to reset: ${resetUrl}`);
    return of(undefined).pipe(delay(100));
  }

  sendAccountLockedEmail(to: string, minutesLocked: number): Observable<void> {
    this.log(
      to, 'account_locked',
      'Segue account locked',
      `Your account has been locked for ${minutesLocked} minutes due to too many failed login attempts.`,
    );
    return of(undefined).pipe(delay(100));
  }

  sendInvitationAcceptedEmail(to: string, firstName: string): Observable<void> {
    this.log(to, 'invitation_accepted', 'Welcome to Segue!', `Dear ${firstName}, your account has been activated. You may now sign in.`);
    return of(undefined).pipe(delay(100));
  }

  private log(to: string, type: string, subject: string, body: string): void {
    const email: MockEmail = { id: `email-${Date.now()}`, to, subject, body, type, sentAt: new Date() };
    this._sentEmails.update(list => [...list, email]);
    console.info(`[EmailService] ${type} email sent to`, to);
  }
}
