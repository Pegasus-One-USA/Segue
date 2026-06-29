import { Component, output } from '@angular/core';
import { RouterLink } from '@angular/router';

interface QuickAction {
  id:     string;
  icon:   string;
  label:  string;
  route?: string;
  accent: 'blue' | 'green' | 'amber' | 'default';
}

const ACTIONS: QuickAction[] = [
  { id: 'new-pipeline', icon: '+',  label: 'New Pipeline', route: '/workflow-builder', accent: 'blue'    },
  { id: 'run-pipeline', icon: '▶',  label: 'Run Pipeline',                             accent: 'green'   },
  { id: 'view-logs',    icon: '≡',  label: 'View Logs',    route: '/logs',             accent: 'default' },
  { id: 'schedule',     icon: '◷',  label: 'Schedule',     route: '/schedules',        accent: 'amber'   },
  { id: 'reports',      icon: '▤',  label: 'Reports',      route: '/reports',          accent: 'default' },
  { id: 'settings',     icon: '⚙',  label: 'Settings',     route: '/config',           accent: 'default' },
];

@Component({
  selector: 'app-quick-actions',
  standalone: true,
  imports: [RouterLink],
  templateUrl: './quick-actions.component.html',
  styleUrl: './quick-actions.component.scss',
})
export class QuickActionsComponent {
  readonly actionClick = output<string>();
  readonly actions = ACTIONS;
}
