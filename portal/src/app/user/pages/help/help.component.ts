import { Component, inject } from '@angular/core';
import { Router }               from '@angular/router';
import { UserSettingsNavComponent } from '../../components/user-settings-nav/user-settings-nav.component';

interface HelpLink { label: string; desc: string; icon: string; }
interface ShortcutRow { action: string; keys: string[]; }

@Component({
  selector:    'app-help',
  standalone:  true,
  imports:     [UserSettingsNavComponent],
  templateUrl: './help.component.html',
  styleUrl:    './help.component.scss',
})
export class HelpComponent {
  private readonly router = inject(Router);

  protected readonly docs: HelpLink[] = [
    { label: 'Documentation', desc: 'Full Segue reference and guides', icon: '📚' },
    { label: 'FHIR R4 Spec', desc: 'Official HL7 FHIR R4 specification', icon: '🏥' },
    { label: 'API Reference', desc: 'REST API docs for integration development', icon: '⚡' },
    { label: 'Release Notes', desc: 'What\'s new in each Segue release', icon: '🎉' },
    { label: 'Video Tutorials', desc: 'Step-by-step video walkthroughs', icon: '▶' },
    { label: 'Community Forum', desc: 'Ask questions, share tips with the community', icon: '💬' },
  ];

  protected readonly shortcuts: ShortcutRow[] = [
    { action: 'Open Node Library',    keys: ['N'] },
    { action: 'Open Command Palette', keys: ['Ctrl', 'K'] },
    { action: 'Save Pipeline',        keys: ['Ctrl', 'S'] },
    { action: 'Undo',                 keys: ['Ctrl', 'Z'] },
    { action: 'Redo',                 keys: ['Ctrl', 'Shift', 'Z'] },
    { action: 'Preview Payload',      keys: ['Ctrl', 'P'] },
    { action: 'Reset Canvas',         keys: ['Ctrl', 'R'] },
    { action: 'Toggle Sidebar',       keys: ['Ctrl', 'B'] },
    { action: 'Go to Dashboard',      keys: ['G', 'D'] },
    { action: 'Go to Pipeline Builder', keys: ['G', 'P'] },
  ];

  navigate(path: string): void { this.router.navigate([path]); }
}
