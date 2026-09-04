import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { WizardServiceV2 } from '../../../../services/wizard-v2.service';
import { ResourceScopeGridComponent } from '../../../shared-v2/resource-scope-grid/resource-scope-grid.component';
import { CTX_TRANSFORMS } from '../../../../data/scope-constants-v2.data';
import { EPIC_APPS } from '../../../../data/epic-apps-v2.data';

@Component({
  selector: 'app-wizard-step2',
  standalone: true,
  imports: [FormsModule, ResourceScopeGridComponent],
  templateUrl: './wizard-step2.component.html',
  styleUrl: './wizard-step2.component.scss',
})
export class WizardStep2Component {
  protected readonly wiz = inject(WizardServiceV2);

  // local form signals for backend/interactive fields
  readonly algorithm  = signal('RS384');
  readonly jwksMethod = signal('hosted');
  readonly jwksUrl    = signal('https://keys.fhirbridge.io/.well-known/jwks.json');
  readonly kid        = signal('fhirbridge-2026-rs384-01');
  readonly kvRef      = signal('kv-fhirbridge://epic/signing-key');
  readonly redirectUri = signal('https://app.fhirbridge.io/epic/callback');
  readonly launchUrl   = signal('https://app.fhirbridge.io/epic/launch');

  // validation errors
  kidError = false;
  jwksUrlError = false;
  redirectError = false;

  protected ctxRows() {
    const app = this.wiz.currentApp();
    return [
      { key: 'Registered app',   val: app.label,                          mono: false },
      { key: 'App context',      val: app.context,                         mono: false },
      { key: 'Environment',      val: this.wiz.currentEnv().label,         mono: false },
      { key: 'Active client ID', val: this.wiz.activeClientId(),           mono: true  },
      { key: 'Scope prefix',     val: app.scopePrefix,                     mono: false },
      { key: 'Flow type',        val: app.interactive ? 'Interactive (browser redirect)' : 'Server-to-server (no user)', mono: false },
    ];
  }

  protected isInteractive() { return this.wiz.currentApp().interactive; }
  protected isEhrLaunch()   { return this.wiz.currentApp().ehrLaunch;   }
  protected isHosted()       { return this.jwksMethod() === 'hosted';    }

  protected transforms() {
    return CTX_TRANSFORMS[this.wiz.currentApp().context] ?? [];
  }

  protected scopeString() { return this.wiz.scopeString(); }
  protected resources()   { return this.wiz.resources();   }

  onResourceToggle(e: { resource: string; checked: boolean }): void {
    this.wiz.toggleResource(e.resource, e.checked);
  }

  /** Called by parent before advancing. Returns false if invalid. */
  validate(): boolean {
    const app = this.wiz.currentApp();
    if (this.wiz.resources().length === 0) {
      return false;
    }
    if (!app.interactive) {
      this.kidError = !this.kid().trim();
      this.jwksUrlError = this.isHosted() && !this.jwksUrl().trim();
      if (this.kidError || this.jwksUrlError) return false;
    } else {
      this.redirectError = !this.redirectUri().trim();
      if (this.redirectError) return false;
    }
    return true;
  }

  getValues() {
    return {
      algorithm:   this.algorithm(),
      jwksMethod:  this.jwksMethod(),
      jwksUrl:     this.jwksUrl(),
      kid:         this.kid(),
      kvRef:       this.kvRef(),
      redirectUri: this.redirectUri(),
      launchUrl:   this.launchUrl(),
    };
  }
}
