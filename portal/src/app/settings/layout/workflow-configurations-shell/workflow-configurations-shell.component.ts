import { Component, inject, computed, signal } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';
import { AuthStore } from '../../../auth/store/auth.store';
import { TransformationRulesService } from '../../../components/node-library/destination-wizard/field-mapping/transformation-rules.service';

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
  { label: 'Transformation Rules', route: 'transformation-rules', icon: 'tune', permissions: ['configuration.write'] },
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
  private readonly transformationRulesSvc = inject(TransformationRulesService);

  // Feature flag: Settings > System Settings > General, "TransformationRules:Hidden" (default false —
  // visible unless an admin explicitly hides it). Starts matching that default until the real value comes
  // back, so there's no flash on first paint in the common case.
  private readonly rulesHidden = signal(false);

  constructor() {
    this.transformationRulesSvc.isHidden().subscribe(hidden => this.rulesHidden.set(hidden));
  }

  readonly sections = computed<WorkflowConfigurationSection[]>(() =>
    WORKFLOW_CONFIGURATION_SECTIONS
      .filter(section => section.route !== 'transformation-rules' || !this.rulesHidden())
      .filter(section => this.store.isAdmin() || section.permissions.some(p => this.store.hasPermission(p)))
  );
}
