import { Component, input, computed } from '@angular/core';

export interface RailStep {
  label: string;
}

@Component({
  selector: 'app-wizard-rail',
  standalone: true,
  imports: [],
  templateUrl: './wizard-rail.component.html',
  styleUrl: './wizard-rail.component.scss',
})
export class WizardRailComponent {
  readonly currentStep = input<number>(1);
  readonly steps = input<RailStep[]>([]);

  stepStatus(index: number): 'done' | 'active' | 'pending' {
    const step = index + 1;
    if (step < this.currentStep()) return 'done';
    if (step === this.currentStep()) return 'active';
    return 'pending';
  }

  circleLabel(index: number): string {
    const step = index + 1;
    return step < this.currentStep() ? '✓' : String(step);
  }
}
