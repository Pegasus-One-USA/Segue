import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { AuthService } from '../../services/auth.service';
import { AuthCardComponent } from '../../components/auth-card/auth-card.component';
import { LoginRequest } from '../../models/auth-request.model';

@Component({
  selector: 'app-login',
  standalone: true,
  imports: [FormsModule, RouterLink, AuthCardComponent],
  templateUrl: './login.component.html',
  styleUrl: './login.component.scss',
})
export class LoginComponent {
  private readonly auth = inject(AuthService);

  protected readonly isLoading = this.auth.isLoading;
  protected readonly error     = this.auth.error;

  protected email    = '';
  protected password = '';
  protected showPw   = signal(false);

  submit(): void {
    const req: LoginRequest = { email: this.email.trim(), password: this.password };
    this.auth.login(req).subscribe();
  }
}
