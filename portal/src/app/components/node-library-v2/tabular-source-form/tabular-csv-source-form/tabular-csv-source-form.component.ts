import { Component } from '@angular/core';
import { ReactiveFormsModule } from '@angular/forms';
import { TabularSourceFile } from '../../../../services/tabular-source.service';
import { TabularCsvFilesComponent } from '../tabular-csv-files/tabular-csv-files.component';
import { TabularDatasetIdentityComponent } from '../tabular-dataset-identity/tabular-dataset-identity.component';
import { newCsvDatasetKey } from '../tabular-dataset-key';
import { TabularSourceFormBase } from '../tabular-source-form-base';
import { TabularStreamCardComponent } from '../tabular-stream-card/tabular-stream-card.component';

/**
 * The "CSV file" source tile: rows come from uploaded CSV files, one per resource type (or one with a type column).
 * The name fills in as "CSV: <first file>". The data set key is made once, when the node is created, and saved with
 * it, so a corrected file or a copied workflow keeps it; a node that already has one keeps its own.
 */
@Component({
  selector: 'app-tabular-csv-source-form',
  standalone: true,
  imports: [ReactiveFormsModule, TabularCsvFilesComponent, TabularStreamCardComponent, TabularDatasetIdentityComponent],
  templateUrl: './tabular-csv-source-form.component.html',
  styleUrl: '../tabular-form-shared.scss',
})
export class TabularCsvSourceFormComponent extends TabularSourceFormBase {
  readonly kind = (): 'csv' => 'csv';
  protected readonly tileName = 'CSV file';

  constructor() {
    super();
    // Replaced by the saved key when an existing node opens (populate runs after this).
    this.form.controls.datasetKey.setValue(newCsvDatasetKey());
  }

  onFileUploaded(file: TabularSourceFile): void {
    this.files.update(list => [...list.filter(f => f.id !== file.id), file]);
    // A type with no file yet reads the one just uploaded.
    this.entries.update(list => list.map(e => (e.fileId ? e : { ...e, fileId: file.id })));
    this.refreshName();
  }

  protected autoName(): string {
    const first = this.files()[0]?.fileName;
    return first ? `CSV: ${first}` : '';
  }
}
