import { Component } from '@angular/core';
import { ReactiveFormsModule } from '@angular/forms';
import { TabularSqlConnection } from '../../../../services/tabular-source.service';
import { TabularDatabasePickerComponent } from '../tabular-database-picker/tabular-database-picker.component';
import { TabularDatasetIdentityComponent } from '../tabular-dataset-identity/tabular-dataset-identity.component';
import { sqlDatasetKey } from '../tabular-dataset-key';
import { TabularSourceFormBase } from '../tabular-source-form-base';
import { TabularStreamCardComponent } from '../tabular-stream-card/tabular-stream-card.component';

/**
 * The "SQL database" source tile: rows come from one query per resource type against a saved database. The name
 * fills in as "SQL: <database>" and the data set key comes from that database, unless the node already has one.
 */
@Component({
  selector: 'app-tabular-sql-source-form',
  standalone: true,
  imports: [ReactiveFormsModule, TabularDatabasePickerComponent, TabularStreamCardComponent, TabularDatasetIdentityComponent],
  templateUrl: './tabular-sql-source-form.component.html',
  styleUrl: '../tabular-form-shared.scss',
})
export class TabularSqlSourceFormComponent extends TabularSourceFormBase {
  readonly kind = (): 'sql' => 'sql';
  protected readonly tileName = 'SQL database';

  onConnection(connection: TabularSqlConnection | null): void {
    this.connection.set(connection);
    this.connectionId.set(connection?.id ?? null);
    if (connection) this.legacySecret.set(null);
    if (!this.keyKept()) this.form.controls.datasetKey.setValue(sqlDatasetKey(connection) ?? '');
    this.refreshName();
  }

  protected autoName(): string {
    const name = this.connection()?.name;
    return name ? `SQL: ${name}` : '';
  }
}
