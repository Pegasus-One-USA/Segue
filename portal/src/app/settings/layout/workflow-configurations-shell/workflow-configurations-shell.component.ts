import { Component, inject, computed } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';
import { AuthStore } from '../../../auth/store/auth.store';

interface WorkflowConfigurationSection {
  label: string;
  route: string;
  icon: string;
  permissions: string[];
}

// Same per-section permission split the three tabs had standalone — Source Connections only needs
// sourceconnections.view, so a source-only user still sees that section even without configuration.write.
const WORKFLOW_CONFIGURATION_SECTIONS: WorkflowConfigurationSection[] = [
  { label: 'Source Connections', route: 'source-connections', icon: 'input', permissions: ['sourceconnections.view'] },
  { label: 'Destination Connections', route: 'destination-connections', icon: 'output', permissions: ['configuration.write'] },
  { label: 'Mapping Profiles', route: 'mapping-profiles', icon: 'swap_horiz', permissions: ['configuration.write'] },
];

@Component({
  selector: 'app-workflow-configurations-shell',
  standalone: true,
  imports: [RouterLink, RouterLinkActive, RouterOutlet, MatIconModule],
  templateUrl: './workflow-configurations-shell.component.html',
  styleUrl: './workflow-configurations-shell.component.scss',
})
export class WorkflowConfigurationsShellComponent {
  private readonly store = inject(AuthStore);

  readonly sections = computed<WorkflowConfigurationSection[]>(() =>
    WORKFLOW_CONFIGURATION_SECTIONS.filter(section =>
      this.store.isAdmin() || section.permissions.some(p => this.store.hasPermission(p))
    )
  );
}
