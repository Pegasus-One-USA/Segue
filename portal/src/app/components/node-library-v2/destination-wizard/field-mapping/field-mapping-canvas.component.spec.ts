import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { FieldMappingCanvasComponent } from './field-mapping-canvas.component';
import { ToastService } from '../../../../services/toast.service';
import { DestinationSchemaService } from '../../../../services/destination-schema.service';
import { TransformationRulesService } from './transformation-rules.service';
import { of, throwError } from 'rxjs';

/**
 * Regression coverage for the "Map Fields table card disappears after Save -> leave -> reopen" bug
 * (MySQL only): the live schema probe qualifies MySQL table names with the connected database's name
 * (e.g. "fhirbridge_output.FamilyMemberHistory", see SqlDestinationSchemaService.ReadColumnsAsync), while
 * a saved/reopened mapping's targetByResource only ever holds the bare table name (bareName(),
 * field-mapping-summary.model.ts). isPrimaryTargetValid() must tolerate that bare-vs-qualified mismatch
 * for MySQL specifically, without loosening SQL Server/PostgreSQL's strict fullName match (their dbo./
 * public. qualification genuinely matches what the live probe returns).
 */
describe('FieldMappingCanvasComponent — MySQL bare-name reopen fix', () => {
  async function createComponent() {
    await TestBed.configureTestingModule({
      imports: [FieldMappingCanvasComponent],
      providers: [
        // The canvas renders field-mapping-list, which injects DeIdentificationProfileService -> HttpClient.
        // Without these the whole suite failed to construct the component at all (10/10 NullInjectorError).
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: ToastService, useValue: { show: () => {} } },
        { provide: DestinationSchemaService, useValue: {} },
        { provide: TransformationRulesService, useValue: {
            // The join popover calls getNodeSchemas() on construction; the rest is what removeRow's own
            // rule cleanup needs.
            getNodeSchemas: () => of([]), getEffectiveRules: () => of([]), delete: () => of(void 0),
          } },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(FieldMappingCanvasComponent);
    return fixture;
  }

  it('MySQL: renders the primary table card after reopen, when targetByResource only holds the bare name a saved mapping restores', async () => {
    const fixture = await createComponent();
    fixture.componentRef.setInput('resources', ['FamilyMemberHistory']);
    fixture.componentRef.setInput('destType', 'mysql');
    fixture.componentRef.setInput('mappingRows', []);
    // Reopen state: targetByResource restored bare (as applyMappingSummaryDocument's bareName() always
    // does for MySQL) — the exact shape a save -> leave -> reopen round trip produces.
    fixture.componentRef.setInput('targetByResource', { FamilyMemberHistory: 'FamilyMemberHistory' });
    fixture.componentRef.setInput('availableFields', () => []);
    fixture.componentRef.setInput('columnsForResourceTarget', () => []);
    fixture.componentRef.setInput('hasSqlTables', true);
    // Live schema probe result: MySQL qualifies with the connected database name.
    fixture.componentRef.setInput('sqlTableOptions', ['fhirbridge_output.FamilyMemberHistory']);
    fixture.componentRef.setInput('sqlTableNames', ['FamilyMemberHistory']);
    fixture.detectChanges();

    const cards = fixture.componentInstance.targetCards();
    expect(cards).toEqual([
      { resource: 'FamilyMemberHistory', tableName: 'FamilyMemberHistory', isExtra: false },
    ]);
  });

  it('MySQL: still hides the primary card when the target matches neither the qualified nor the bare live-schema list', async () => {
    const fixture = await createComponent();
    fixture.componentRef.setInput('resources', ['FamilyMemberHistory']);
    fixture.componentRef.setInput('destType', 'mysql');
    fixture.componentRef.setInput('mappingRows', []);
    fixture.componentRef.setInput('targetByResource', { FamilyMemberHistory: 'SomeOtherTable' });
    fixture.componentRef.setInput('availableFields', () => []);
    fixture.componentRef.setInput('columnsForResourceTarget', () => []);
    fixture.componentRef.setInput('hasSqlTables', true);
    fixture.componentRef.setInput('sqlTableOptions', ['fhirbridge_output.FamilyMemberHistory']);
    fixture.componentRef.setInput('sqlTableNames', ['FamilyMemberHistory']);
    fixture.detectChanges();

    expect(fixture.componentInstance.targetCards()).toEqual([]);
  });

  it('SQL Server: a bare-name-only match is NOT tolerated — strict fullName matching is unchanged', async () => {
    const fixture = await createComponent();
    fixture.componentRef.setInput('resources', ['Patient']);
    fixture.componentRef.setInput('destType', 'sql');
    fixture.componentRef.setInput('mappingRows', []);
    // Same shape of mismatch as the MySQL case above (bare "Patient" vs qualified "dbo.Patient") —
    // for SQL Server this must NOT be tolerated, since dbo. qualification genuinely matches the live probe.
    fixture.componentRef.setInput('targetByResource', { Patient: 'Patient' });
    fixture.componentRef.setInput('availableFields', () => []);
    fixture.componentRef.setInput('columnsForResourceTarget', () => []);
    fixture.componentRef.setInput('hasSqlTables', true);
    fixture.componentRef.setInput('sqlTableOptions', ['dbo.Patient']);
    fixture.componentRef.setInput('sqlTableNames', ['Patient']); // present but must be ignored for non-MySQL
    fixture.detectChanges();

    expect(fixture.componentInstance.targetCards()).toEqual([]);
  });

  it('PostgreSQL: a bare-name-only match is NOT tolerated — strict fullName matching is unchanged', async () => {
    const fixture = await createComponent();
    fixture.componentRef.setInput('resources', ['Patient']);
    fixture.componentRef.setInput('destType', 'postgres');
    fixture.componentRef.setInput('mappingRows', []);
    fixture.componentRef.setInput('targetByResource', { Patient: 'Patient' });
    fixture.componentRef.setInput('availableFields', () => []);
    fixture.componentRef.setInput('columnsForResourceTarget', () => []);
    fixture.componentRef.setInput('hasSqlTables', true);
    fixture.componentRef.setInput('sqlTableOptions', ['public.Patient']);
    fixture.componentRef.setInput('sqlTableNames', ['Patient']);
    fixture.detectChanges();

    expect(fixture.componentInstance.targetCards()).toEqual([]);
  });

  it('MySQL: extra/child table cards are restored and rendered regardless of qualification (no validity gate on extras)', async () => {
    const fixture = await createComponent();
    fixture.componentRef.setInput('resources', ['FamilyMemberHistory']);
    fixture.componentRef.setInput('destType', 'mysql');
    fixture.componentRef.setInput('mappingRows', []);
    fixture.componentRef.setInput('targetByResource', { FamilyMemberHistory: 'FamilyMemberHistory' });
    fixture.componentRef.setInput('availableFields', () => []);
    fixture.componentRef.setInput('columnsForResourceTarget', () => []);
    fixture.componentRef.setInput('hasSqlTables', true);
    fixture.componentRef.setInput('sqlTableOptions', ['fhirbridge_output.FamilyMemberHistory']);
    fixture.componentRef.setInput('sqlTableNames', ['FamilyMemberHistory']);
    // A restored extra/child table, saved bare (same round trip as the primary target).
    fixture.componentRef.setInput('extraTables', ['FamilyMemberHistory_Contact']);
    fixture.detectChanges();

    const cards = fixture.componentInstance.targetCards();
    expect(cards).toEqual([
      { resource: 'FamilyMemberHistory', tableName: 'FamilyMemberHistory', isExtra: false },
      { resource: 'FamilyMemberHistory', tableName: 'FamilyMemberHistory_Contact', isExtra: true },
    ]);
  });
});


/**
 * Removing a mapping must take its transformation rule with it. Rules are keyed server-side by
 * (resource, destination field, source field) rather than by the mapping row, so a row deleted on its own
 * left the rule resolvable — and a mapping later recreated onto the same column silently picked it back up.
 */
describe('FieldMappingCanvasComponent — removing a mapping deletes its transformation rule', () => {
  const ROW = {
    resource: 'Observation',
    sources: [{ fhirPath: 'Observation.valueQuantity.value', label: 'value' }],
    mode: 'value' as const,
    targetName: 'TransformationQuantityRange',
    tableName: 'public.Observation',
  };

  async function createComponent(rulesService: unknown) {
    await TestBed.configureTestingModule({
      imports: [FieldMappingCanvasComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        // Toasts are irrelevant to what these cases assert — stubbed to no-ops so a component that raises
        // one does not need a real ToastService (and so an unexpected toast never fails an unrelated case).
        // Every level the component can raise, not just the three it happened to use when this was written —
        // a missing one fails as "this.toast.X is not a function" from whatever code path reaches it, which
        // reads like a bug in the code under test rather than a gap in the harness.
        {
          provide: ToastService,
          useValue: {
            show: () => { /* no-op stub */ },
            success: () => { /* no-op stub */ },
            info: () => { /* no-op stub */ },
            warning: () => { /* no-op stub */ },
            error: () => { /* no-op stub */ },
          },
        },
        { provide: DestinationSchemaService, useValue: {} },
        { provide: TransformationRulesService, useValue: { getNodeSchemas: () => of([]), ...(rulesService as object) } },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(FieldMappingCanvasComponent);
    fixture.componentRef.setInput('resources', ['Observation']);
    fixture.componentRef.setInput('mappingRows', [ROW]);
    fixture.componentRef.setInput('rulesDestinationType', 'PostgreSql');
    fixture.componentRef.setInput('workflowId', 'wf-1');
    fixture.componentRef.setInput('availableFields', () => []);
    fixture.componentRef.setInput('columnsForResourceTarget', () => []);
    return fixture;
  }

  it('deletes the rule the removed row owned', async () => {
    const deleted: string[] = [];
    const fixture = await createComponent({
      getEffectiveRules: () => of([{ id: 'rule-1', resourcePipelineRouteId: 'wf-1' }] as never),
      delete: (id: string) => { deleted.push(id); return of(void 0); },
    });

    fixture.componentInstance.removeRow('Observation', 'public.Observation', 'TransformationQuantityRange');

    expect(deleted).toEqual(['rule-1']);
  });

  it('looks the rule up scoped to this workflow only, so a shared tenant-wide rule is never deleted', async () => {
    let query: Record<string, unknown> | undefined;
    const fixture = await createComponent({
      getEffectiveRules: (q: Record<string, unknown>) => { query = q; return of([] as never); },
      delete: () => of(void 0),
    });

    fixture.componentInstance.removeRow('Observation', 'public.Observation', 'TransformationQuantityRange');

    expect(query?.['workflowScopedOnly']).toBeTrue();
    expect(query?.['destinationField']).toBe('TransformationQuantityRange');
    expect(query?.['sourceField']).toBe('Observation.valueQuantity.value');
    expect(query?.['resourcePipelineRouteId']).toBe('wf-1');
    // NOT pending. workflowScopedOnly does not scope the pending tier — GetPendingWorkflowScopedAsync has no
    // workflow id in its predicate at all — so asking for pending rows here would let deleting a mapping in
    // one pipeline hard-delete an unsaved draft rule belonging to a different one.
    expect(query?.['includePending']).toBeFalsy();
  });

  it('never deletes a rule attached to a different workflow, even if the server returns one', async () => {
    const deleted: string[] = [];
    const fixture = await createComponent({
      getEffectiveRules: () => of([
        { id: 'mine', resourcePipelineRouteId: 'wf-1' },
        { id: 'other-workflow', resourcePipelineRouteId: 'wf-2' },
        { id: 'unattached-draft', resourcePipelineRouteId: null },
        { id: 'tenant-wide' },
      ] as never),
      delete: (id: string) => { deleted.push(id); return of(void 0); },
    });

    fixture.componentInstance.removeRow('Observation', 'public.Observation', 'TransformationQuantityRange');

    expect(deleted).toEqual(['mine']);
  });

  it('deletes nothing when there is no workflow id to prove ownership with', async () => {
    let called = false;
    const fixture = await createComponent({
      getEffectiveRules: () => { called = true; return of([] as never); },
      delete: () => of(void 0),
    });
    fixture.componentRef.setInput('workflowId', null);

    fixture.componentInstance.removeRow('Observation', 'public.Observation', 'TransformationQuantityRange');

    expect(called).toBeFalse();
  });

  it('still removes the row when the rule lookup fails', async () => {
    const emitted: unknown[] = [];
    const fixture = await createComponent({
      getEffectiveRules: () => throwError(() => new Error('offline')),
      delete: () => of(void 0),
    });
    fixture.componentInstance.mappingRowsChange.subscribe((rows: unknown) => emitted.push(rows));

    expect(() =>
      fixture.componentInstance.removeRow('Observation', 'public.Observation', 'TransformationQuantityRange'),
    ).not.toThrow();
    expect(emitted).toEqual([[]]);
  });

  it('does nothing rule-side when the row was not there to begin with', async () => {
    let called = false;
    const fixture = await createComponent({
      getEffectiveRules: () => { called = true; return of([] as never); },
      delete: () => of(void 0),
    });

    fixture.componentInstance.removeRow('Observation', 'public.Observation', 'NoSuchColumn');

    expect(called).toBeFalse();
  });

  // A REPLACE drops the previous row just as surely as a delete does. Whether that strands the rule depends
  // entirely on whether the replacement still carries the source field the rule is keyed on, so both
  // directions need pinning: remapping the column away from the field must clean up, joining a second source
  // onto the same field must NOT.
  it('deletes the rule when a column is re-pointed at a literal default, discarding its source', async () => {
    const deleted: string[] = [];
    const fixture = await createComponent({
      getEffectiveRules: () => of([{ id: 'rule-1', resourcePipelineRouteId: 'wf-1' }] as never),
      delete: (id: string) => { deleted.push(id); return of(void 0); },
    });

    fixture.componentInstance.openDefaultValueModal('Observation', 'public.Observation', 'TransformationQuantityRange');
    fixture.componentInstance.submitDefaultValue({ token: '@default', literalValue: 'n/a', valueType: 'String' });

    expect(deleted).toEqual(['rule-1']);
  });

  it('keeps the rule when a second source is JOINED onto the column, which leaves sources[0] intact', async () => {
    let called = false;
    const fixture = await createComponent({
      getEffectiveRules: () => { called = true; return of([] as never); },
      delete: () => of(void 0),
    });
    const component = fixture.componentInstance as unknown as {
      replaceRow(resource: string, tableName: string, column: string, row: unknown): void;
    };

    // What completeMapping's join branch builds: the ORIGINAL primary source, plus a new one appended.
    component.replaceRow('Observation', 'public.Observation', 'TransformationQuantityRange', {
      ...ROW,
      sources: [ROW.sources[0], { fhirPath: 'Observation.valueQuantity.unit', label: 'unit' }],
      delimiter: ', ',
    });

    expect(called).toBeFalse();
  });

  // updateRow edits a row IN PLACE, so it never changes the row count — which is why its cleanup was missing
  // while three separate doc comments claimed the caller list was complete. The join popover rekeys a mapping
  // through it without removing anything.
  it('deletes the rule when the FIRST chip of a join is removed, promoting sources[1] to the key', async () => {
    const deleted: string[] = [];
    const fixture = await createComponent({
      getEffectiveRules: () => of([{ id: 'rule-1', resourcePipelineRouteId: 'wf-1' }] as never),
      delete: (id: string) => { deleted.push(id); return of(void 0); },
    });
    const joined = {
      ...ROW,
      sources: [ROW.sources[0], { fhirPath: 'Observation.valueString', label: 'valueString' }],
    };
    fixture.componentRef.setInput('mappingRows', [joined]);

    // What removeSource(0) leaves behind: valueString is now sources[0], so the rule keyed on
    // valueQuantity.value no longer has a mapping.
    fixture.componentInstance.updateRow({ ...joined, sources: [joined.sources[1]] });

    expect(deleted).toEqual(['rule-1']);
  });

  it('deletes the rule when the chips are REORDERED so a different source leads', async () => {
    const deleted: string[] = [];
    const fixture = await createComponent({
      getEffectiveRules: () => of([{ id: 'rule-1', resourcePipelineRouteId: 'wf-1' }] as never),
      delete: (id: string) => { deleted.push(id); return of(void 0); },
    });
    const second = { fhirPath: 'Observation.valueString', label: 'valueString' };
    const joined = { ...ROW, sources: [ROW.sources[0], second] };
    fixture.componentRef.setInput('mappingRows', [joined]);

    fixture.componentInstance.updateRow({ ...joined, sources: [second, ROW.sources[0]] });

    expect(deleted).toEqual(['rule-1']);
  });

  it('keeps the rule when updateRow changes something other than the key', async () => {
    let called = false;
    const fixture = await createComponent({
      getEffectiveRules: () => { called = true; return of([] as never); },
      delete: () => of(void 0),
    });

    // Editing the instance selection leaves resource/targetName/sources[0] alone — the rule still has its
    // mapping and must not be touched.
    fixture.componentInstance.updateRow({ ...ROW, instance: { type: 'nth', index: 2 } } as never);

    expect(called).toBeFalse();
  });

  it('does NOT delete the rule when a destination column is renamed', async () => {
    let called = false;
    const fixture = await createComponent({
      getEffectiveRules: () => { called = true; return of([] as never); },
      delete: () => of(void 0),
    });

    // onRenameFreeColumn consults the live column list, which needs these two inputs. false = no probed
    // schema, so it falls back to the mapped + pending free-text names — the free-column rename path.
    fixture.componentRef.setInput('hasSqlTables', false);
    fixture.componentRef.setInput('destType', 'postgres');

    const emitted: { targetName: string }[][] = [];
    fixture.componentInstance.mappingRowsChange.subscribe(rows => { emitted.push(rows); });

    // A rename changes targetName, which is half the rule's key — so the diff WOULD see the old key vanish.
    // Deleting there would destroy a rule the author still wants for renaming a column, which is strictly
    // worse than the orphan it avoids, so the rename path deliberately bypasses reconciliation.
    fixture.componentInstance.onRenameFreeColumn(
      'Observation', 'public.Observation', 'TransformationQuantityRange', 'renamed_col');

    // Asserted so the case cannot pass merely because the rename bailed out before doing anything.
    expect(emitted.at(-1)?.[0].targetName).toBe('renamed_col');
    expect(called).toBeFalse();
  });

  // "Reset to Original" is an UNDO. Everything else it does is local and reversible — the rows are in memory
  // until Save — so it must not issue an irreversible server-side DELETE. An author who resets an experiment
  // and then abandons the wizard would otherwise keep their (never-saved) mappings and silently lose every
  // rule for the resource, with no undo and no message saying so.
  it('does NOT delete rules when "Reset to Original" clears the mappings', async () => {
    let called = false;
    const fixture = await createComponent({
      getEffectiveRules: () => { called = true; return of([] as never); },
      delete: () => of(void 0),
    });
    const emitted: unknown[][] = [];
    fixture.componentInstance.mappingRowsChange.subscribe(rows => { emitted.push(rows); });

    fixture.componentInstance.onResetToOriginalPayload();

    // The rows really were cleared — so this cannot pass by the reset having done nothing at all.
    expect(emitted.at(-1)).toEqual([]);
    expect(called).toBeFalse();
  });

  // The other caller of the same clearing helper. A pasted payload genuinely replaces this resource's fields,
  // so the old fhirPaths are gone and rules keyed on them are dead by construction — that one still cleans up.
  it('DOES delete rules when a new payload replaces the resource fields', async () => {
    const deleted: string[] = [];
    const fixture = await createComponent({
      getEffectiveRules: () => of([{ id: 'rule-1', resourcePipelineRouteId: 'wf-1' }] as never),
      delete: (id: string) => { deleted.push(id); return of(void 0); },
    });

    fixture.componentInstance.submitLoadPayload(
      JSON.stringify({ resourceType: 'Observation', id: 'obs-1', status: 'final' }));

    expect(deleted).toEqual(['rule-1']);
  });
});
