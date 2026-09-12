/**
 * Typed encoder for the ContributionSnapshotV1 protobuf wire format.
 *
 * Maps the JSON payload types from `src/models/requests.ts` onto the .proto
 * field numbers in `proto/contribution_snapshot_v1.proto`. Used by the export
 * engine to stream entity rows directly into protobuf (no intermediate JSON).
 */
import { ProtoWriter } from './writer';
import type {
  ContributionMetadataEntityPayload,
  ContributionRecordV1Payload,
  ContributionSeriesEntityPayload,
  ContributionSourceEntityPayload,
  MappingTitleEntityPayload,
  TitleEntityPayload,
} from '../models/requests';

// Field numbers in ContributionSnapshotV1 (see proto file).
export const SNAPSHOT_FIELD_SCHEMA_VERSION = 1;
export const SNAPSHOT_FIELD_GENERATED_UTC = 2;
export const SNAPSHOT_FIELD_VERSION = 3;
export const SNAPSHOT_FIELD_TITLES = 4;
export const SNAPSHOT_FIELD_MAPPING_TITLES = 5;
export const SNAPSHOT_FIELD_SOURCES = 6;
export const SNAPSHOT_FIELD_SERIES = 7;
export const SNAPSHOT_FIELD_METADATA = 8;

/** Encode a TitleEntity row. */
export function encodeTitle(w: ProtoWriter, row: TitleEntityPayload): void {
  const m = new ProtoWriter();
  m.string(1, row.i);
  m.string(2, row.t);
  m.int32(3, row.v);
  w.message(SNAPSHOT_FIELD_TITLES, m.finish());
}

/** Encode a MappingTitleEntity row. */
export function encodeMappingTitle(w: ProtoWriter, row: MappingTitleEntityPayload): void {
  const m = new ProtoWriter();
  m.string(1, row.m);
  m.string(2, row.t);
  m.int32(3, row.v);
  w.message(SNAPSHOT_FIELD_MAPPING_TITLES, m.finish());
}

/** Encode a ContributionSourceEntity row. */
export function encodeSource(w: ProtoWriter, row: ContributionSourceEntityPayload): void {
  const m = new ProtoWriter();
  m.string(1, row.i);
  m.string(2, row.p);
  m.int64(3, row.s);
  m.string(4, row.n);
  m.string(5, row.l);
  m.string(6, row.b);
  m.int32(7, row.v);
  w.message(SNAPSHOT_FIELD_SOURCES, m.finish());
}

/** Encode a ContributionRecordV1 (field numbers per proto message). */
export function encodeContributionRecord(w: ProtoWriter, row: ContributionRecordV1Payload): void {
  w.uint32(1, row.v);
  w.string(2, row.i);
  w.string(3, row.t);
  w.int32(4, row.s);
  w.bool(5, row.p);
  w.bool(6, row.e);
  w.double(7, row.l);
  w.string(8, row.u);
}

/** Encode a ContributionSeriesEntity row (embeds the record). */
export function encodeSeries(w: ProtoWriter, row: ContributionSeriesEntityPayload): void {
  const m = new ProtoWriter();
  m.string(1, row.i);
  m.string(2, row.m);
  m.string(3, row.s);
  const rec = new ProtoWriter();
  encodeContributionRecord(rec, row.d);
  m.message(4, rec.finish());
  m.int32(5, row.v);
  w.message(SNAPSHOT_FIELD_SERIES, m.finish());
}

/** Encode a ContributionMetadataEntity row. */
export function encodeMetadata(w: ProtoWriter, row: ContributionMetadataEntityPayload): void {
  const m = new ProtoWriter();
  m.string(1, row.i);
  m.string(2, row.m);
  m.int32(3, row.p);
  m.string(4, row.k);
  m.int32(5, row.s);
  m.string(6, row.u);
  m.int32(7, row.v);
  w.message(SNAPSHOT_FIELD_METADATA, m.finish());
}