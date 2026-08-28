import { Component, computed, input, output } from '@angular/core';
import { MappingRow, MappingDestType, isSqlFamilyDestType } from './field-mapping-model';
import { buildSqlInsert, buildCsvPreview, CSV_DELIMITERS } from './field-mapping-preview.util';

interface FmPreviewSection {
  resource: string;
  tableName: string;
  text: string;
}

/**
 * Slide-in-from-right output preview — SQL INSERT statements or a CSV header+sample-row, per the
 * scope decision to cover only the two destination types this wizard actually configures (not the
 * mockup's REST/JSON/NoSQL/Excel generators).
 */
@Component({
  selector: 'app-field-mapping-preview-drawer',
  standalone: true,
  imports: [],
  templateUrl: './field-mapping-preview-drawer.component.html',
  styleUrl: './field-mapping-preview-drawer.component.scss',
})
export class FieldMappingPreviewDrawerComponent {
  readonly open = input.required<boolean>();
  readonly resources = input.required<string[]>();
  readonly rows = input.required<MappingRow[]>();
  readonly destType = input.required<MappingDestType>();
  readonly targetByResource = input.required<Record<string, string>>();
  readonly csvDelimiterKey = input<string>('comma');

  readonly closed = output<void>();

  /** SQL Server/MySQL/PostgreSQL all render as INSERT statements here — only CSV (and Mongo/Blob, which
   *  never reach this drawer today) get the flat-file preview. See isSqlFamilyDestType's doc comment for
   *  why this can't be a bare `=== 'sql'` check. */
  readonly isSqlFamily = computed(() => isSqlFamilyDestType(this.destType()));

  readonly sections = computed<FmPreviewSection[]>(() => {
    const delimiter = CSV_DELIMITERS[this.csvDelimiterKey()] ?? ',';
    return this.resources().map(resource => {
      const rowsForResource = this.rows().filter(r => r.resource === resource);
      const tableName = this.targetByResource()[resource] ?? '';
      const text = this.isSqlFamily()
        ? buildSqlInsert(tableName, rowsForResource)
        : buildCsvPreview(rowsForResource, delimiter);
      return { resource, tableName, text };
    });
  });
}
