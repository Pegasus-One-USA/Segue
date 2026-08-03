/** Must match the backend's MappingValueType enum member names (serialized as strings). */
export type MappingValueType = 'String' | 'Integer' | 'Decimal' | 'Boolean' | 'Date' | 'DateTime' | 'Json';

/** Must match the backend's ArrayPolicy enum member names. */
export type ArrayPolicyType =
  | 'Scalar'
  | 'FirstItem'
  | 'RepeatParent'
  | 'SeparateDestination'
  | 'StoreJson'
  | 'RejectIfMultiple'
  | 'CorrelateByCode';

/** Matches the API's MappingFieldDto shape exactly (see MappingFieldDto.cs), minus MaxLength/Precision/Scale —
 *  those are never persisted on the profile itself, only filled in at pipeline run time from the destination's
 *  live schema, so this screen (which has no live probe) never sends them. */
export interface MappingFieldDto {
  targetField: string;
  jsonPath: string;
  valueType: MappingValueType;
  isRequired: boolean;
  defaultValue?: string | null;
  format?: string | null;
  resourceType?: string | null;
  destinationObject?: string | null;
  normalizationType?: string | null;
  terminologySystemJsonPath?: string | null;
  terminologyCodeJsonPath?: string | null;
  arrayPolicy?: ArrayPolicyType;
  cardinality?: string | null;
  arrayAncestors?: string[] | null;
  isUpsertKey?: boolean;
  correlationCodeJsonPath?: string | null;
  correlationCodeValue?: string | null;
  isEnabled?: boolean;
}

/** Matches the API's MappingProfileDto shape exactly, so no DTO<->model mapping is needed. */
export interface MappingProfileDto {
  id: string;
  name: string;
  resourceType: string;
  sourceConnectionId: string;
  destinationId: string;
  destinationObject: string;
  fields: MappingFieldDto[];
  isEnabled: boolean;
  createdOnUtc?: string | null;
  createdBy?: string | null;
  modifiedOnUtc?: string | null;
  modifiedBy?: string | null;
  sourceConfigurationId?: string | null;
}

export interface CreateMappingProfileRequest {
  name: string;
  resourceType: string;
  sourceConnectionId: string;
  destinationId: string;
  destinationObject: string;
  fields: MappingFieldDto[];
  sourceConfigurationId?: string | null;
}

export interface PagedResult<T> {
  items: T[];
  totalCount: number;
  page: number;
  pageSize: number;
}

export type MappingProfileSortColumn =
  | 'name'
  | 'resourceType'
  | 'destinationObject'
  | 'isEnabled'
  | 'createdOnUtc'
  | 'modifiedOnUtc';
export type SortOrder = 'asc' | 'desc';

export interface MappingProfileFilter {
  search?: string;
  resourceType?: string;
  sourceConnectionId?: string;
  destinationId?: string;
  isEnabled?: boolean;
  sortBy?: MappingProfileSortColumn;
  sortOrder?: SortOrder;
  page: number;
  pageSize: number;
}
