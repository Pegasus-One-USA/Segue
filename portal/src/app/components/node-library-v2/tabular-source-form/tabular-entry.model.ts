/** One resource type a CSV / SQL Table source reads, as edited in the form. The template is text until saved. */
export interface TabularEntry {
  resourceType: string;
  query: string;
  fileId: string;
  rowFilterColumn: string;
  rowFilterValue: string;
  template: string;
}
