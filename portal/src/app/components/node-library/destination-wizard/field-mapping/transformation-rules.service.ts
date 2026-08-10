import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, catchError, map, of, shareReplay } from 'rxjs';
import { TRANSFORMATION_RULES_ENDPOINTS } from '../../../../core/api-endpoints';
import { DestinationType } from '../../../../destination-connections/models/destination-configuration.model';

export type TransformScope = 'Global' | 'DestinationType' | 'ResourceType' | 'Field' | 'Workflow';

/** The 20 field-level FHIR-aware transform nodes — FHIRBridge_Top20_Transformations.pdf v1.0. */
export type TransformNodeType =
  | 'DateTimeFormat' | 'NumberCast' | 'BooleanConversion' | 'UnitConversion' | 'QuantityRangeAssembly'
  | 'RoundingScaling' | 'ValueCodeMapping' | 'CodeableConceptBuilder' | 'StatusEnumCoercion'
  | 'ReferenceConstruction' | 'IdentifierFormatting' | 'HumanNameParsing' | 'AddressParsing'
  | 'TelecomNormalization' | 'StringNormalization' | 'ConcatenationTemplating' | 'ArrayListOperations'
  | 'DefaultNullHandling' | 'DateMathAge' | 'HashingMasking';

export type NullPolicy = 'Skip' | 'Default' | 'Error';
export type TransformErrorPolicy = 'Fail' | 'NullOut' | 'PassThrough' | 'RouteToDeadLetter';
export type TransformArrayMode = 'Whole' | 'PerItem';

export interface TransformationRule {
  id: string;
  scope: TransformScope;
  destinationType?: DestinationType | null;
  resourceType?: string | null;
  destinationField?: string | null;
  resourcePipelineRouteId?: string | null;
  /** Only meaningful at Field/Workflow scope — null means "any source"; a value (e.g. "Epic") narrows this
   *  rule to that source only, and wins over a null-sourceSystem row at the same tier. */
  sourceSystem?: string | null;
  /** The FHIR path (e.g. "identifier.value") this rule was authored against — complementary to
   *  destinationField, not a replacement: this is "what shape is the input," destinationField is "what
   *  shape must the output become." Null means "any source field." */
  sourceField?: string | null;
  nodeType: TransformNodeType;
  config: Record<string, string>;
  order: number;
  onNull: NullPolicy;
  errorPolicy: TransformErrorPolicy;
  isEnabled: boolean;
  /** Substitute value used when onNull is 'Default' — the node is skipped and this becomes the output. */
  onNullDefaultValue?: string | null;
  /** 'Whole' (default) runs the node once against the value as-is; 'PerItem' runs it once per element when
   *  the mapped value is a real collection. */
  arrayMode: TransformArrayMode;
}

export interface SaveTransformationRuleRequest {
  id?: string | null;
  scope: TransformScope;
  nodeType: TransformNodeType;
  config: Record<string, string>;
  destinationType?: DestinationType | null;
  resourceType?: string | null;
  destinationField?: string | null;
  resourcePipelineRouteId?: string | null;
  sourceSystem?: string | null;
  sourceField?: string | null;
  order?: number;
  onNull?: NullPolicy;
  errorPolicy?: TransformErrorPolicy;
  isEnabled?: boolean;
  onNullDefaultValue?: string | null;
  arrayMode?: TransformArrayMode;
}

export interface TransformPreviewRequest {
  destinationType: DestinationType;
  resourceType: string;
  destinationField: string;
  sampleValue: unknown;
  resourcePipelineRouteId?: string | null;
  sourceSystem?: string | null;
  sourceField?: string | null;
}

export type ConfigFieldInputKind = 'text' | 'select' | 'checkbox';

export interface TransformConfigFieldSchema {
  key: string;
  label: string;
  inputKind: ConfigFieldInputKind;
  options?: string[] | null;
  defaultValue?: string | null;
  /** Grey example text shown inside an empty box — never submitted as the real value, unlike defaultValue. */
  placeholder?: string | null;
}

export interface TransformNodeSchema {
  nodeType: TransformNodeType;
  label: string;
  fields: TransformConfigFieldSchema[];
}

export interface TransformStepTrace {
  nodeType: TransformNodeType;
  scope: TransformScope;
  inputValue: unknown;
  outputValue: unknown;
  success: boolean;
  error?: string | null;
}

export interface TransformPreviewResult {
  finalValue: unknown;
  effectiveScope: TransformScope | null;
  steps: TransformStepTrace[];
}

/**
 * Backend for the destination wizard's "Rules" button/modal: lists whatever's configured at any scope for a
 * resource type, saves/deletes individual rule rows, and resolves-then-applies a sample value through
 * whichever rule chain is currently in effect (TransformationRulesController, api/v1/transformation-rules).
 */
@Injectable({ providedIn: 'root' })
export class TransformationRulesService {
  private readonly http = inject(HttpClient);

  private nodeSchemasCache: Observable<TransformNodeSchema[]> | null = null;
  private hiddenCache: Observable<boolean> | null = null;

  list(filter: {
    scope?: TransformScope; destinationType?: DestinationType; resourceType?: string;
    destinationField?: string; resourcePipelineRouteId?: string; sourceSystem?: string; sourceField?: string;
  }): Observable<TransformationRule[]> {
    const params = new URLSearchParams();
    Object.entries(filter).forEach(([key, value]) => {
      if (value !== undefined && value !== null) params.set(key, String(value));
    });
    const qs = params.toString();
    return this.http.get<TransformationRule[]>(qs ? `${TRANSFORMATION_RULES_ENDPOINTS.list}?${qs}` : TRANSFORMATION_RULES_ENDPOINTS.list);
  }

  /** The actual rule(s) currently in effect for one field (real id + full config, not a value trace) — lets
   *  the wizard's Rules dialog show/clone what's really running instead of starting an override from blank
   *  schema defaults. Resolves the same Workflow &gt; Field &gt; ResourceType &gt; DestinationType &gt; Global
   *  chain as preview(). */
  getEffectiveRules(filter: {
    destinationType: DestinationType; resourceType: string; destinationField: string;
    resourcePipelineRouteId?: string; sourceSystem?: string | null; sourceField?: string | null;
  }): Observable<TransformationRule[]> {
    const params = new URLSearchParams();
    Object.entries(filter).forEach(([key, value]) => {
      if (value !== undefined && value !== null) params.set(key, String(value));
    });
    return this.http.get<TransformationRule[]>(`${TRANSFORMATION_RULES_ENDPOINTS.effective}?${params.toString()}`);
  }

  save(request: SaveTransformationRuleRequest): Observable<TransformationRule> {
    return this.http.post<TransformationRule>(TRANSFORMATION_RULES_ENDPOINTS.save, request);
  }

  delete(ruleId: string): Observable<void> {
    return this.http.delete<void>(TRANSFORMATION_RULES_ENDPOINTS.delete(ruleId));
  }

  preview(request: TransformPreviewRequest): Observable<TransformPreviewResult> {
    return this.http.post<TransformPreviewResult>(TRANSFORMATION_RULES_ENDPOINTS.preview, request);
  }

  /** Cached for the app session — this is static reference data (which config keys each node type reads),
   *  not per-user state. */
  getNodeSchemas(): Observable<TransformNodeSchema[]> {
    if (!this.nodeSchemasCache) {
      this.nodeSchemasCache = this.http.get<TransformNodeSchema[]>(TRANSFORMATION_RULES_ENDPOINTS.nodeSchemas)
        .pipe(shareReplay(1));
    }
    return this.nodeSchemasCache;
  }

  /** Whether the whole feature is currently hidden (Settings &gt; System Settings &gt; General,
   *  "TransformationRules:Hidden", default false). Cached for the app session like getNodeSchemas() — a
   *  System Setting change takes effect on next portal reload, not mid-session. Fails safe to `false`
   *  (visible) if the call errors, since that's the feature's own default. */
  isHidden(): Observable<boolean> {
    if (!this.hiddenCache) {
      this.hiddenCache = this.http.get<{ hidden: boolean }>(TRANSFORMATION_RULES_ENDPOINTS.hidden)
        .pipe(map(r => r.hidden), catchError(() => of(false)), shareReplay(1));
    }
    return this.hiddenCache;
  }
}
