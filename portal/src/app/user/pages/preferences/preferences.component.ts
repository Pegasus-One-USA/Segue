import { Component, inject, signal } from '@angular/core';
import { Router }               from '@angular/router';
import { FormsModule }          from '@angular/forms';
import { UserSettingsNavComponent } from '../../components/user-settings-nav/user-settings-nav.component';
import { UserProfileService }   from '../../services/user-profile.service';
import { AppTheme }             from '../../models/user-profile.model';

@Component({
  selector:    'app-preferences',
  standalone:  true,
  imports:     [FormsModule, UserSettingsNavComponent],
  templateUrl: './preferences.component.html',
  styleUrl:    './preferences.component.scss',
})
export class PreferencesComponent {
  private readonly router  = inject(Router);
  protected readonly profSvc = inject(UserProfileService);

  protected readonly saved = signal(false);

  protected readonly profile = this.profSvc.profile;
  protected readonly theme   = this.profSvc.theme;

  protected readonly themeOptions: { id: AppTheme; label: string; desc: string }[] = [
    { id: 'light',  label: 'Light',  desc: 'Classic light interface' },
    { id: 'dark',   label: 'Dark',   desc: 'Easy on the eyes at night' },
    { id: 'system', label: 'System', desc: 'Follows your OS setting' },
  ];

  protected readonly languages = [
    'English (US)', 'English (UK)', 'Spanish (ES)', 'French (FR)',
    'German (DE)', 'Portuguese (BR)', 'Japanese (JA)',
  ];
  protected readonly dateFormats = ['MM/DD/YYYY', 'DD/MM/YYYY', 'YYYY-MM-DD'];
  protected readonly timeFormats: ('12h' | '24h')[] = ['12h', '24h'];

  protected selectedLanguage  = this.profile().language;
  protected selectedDateFmt   = this.profile().dateFormat;
  protected selectedTimeFmt   = this.profile().timeFormat;
  protected emailNotif        = this.profile().emailNotifications;
  protected inAppNotif        = this.profile().inAppNotifications;

  navigate(path: string): void { this.router.navigate([path]); }

  setTheme(t: AppTheme): void { this.profSvc.setTheme(t); }

  savePreferences(): void {
    this.profSvc.updateProfile({
      language:  this.selectedLanguage,
      dateFormat: this.selectedDateFmt,
      timeFormat: this.selectedTimeFmt,
    });
    this.profSvc.updateNotifications(this.emailNotif, this.inAppNotif);
    this.saved.set(true);
    setTimeout(() => this.saved.set(false), 3000);
  }
}
