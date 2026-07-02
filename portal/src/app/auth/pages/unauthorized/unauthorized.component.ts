// auth/pages/unauthorized/unauthorized.component.ts
import { Component } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';

@Component({
  selector: 'app-unauthorized',
  standalone: true,
  imports: [RouterLink, MatButtonModule, MatIconModule],
  templateUrl: './unauthorized.component.html',
  styleUrl: './unauthorized.component.scss',
})
export class UnauthorizedComponent {}
