// Generated from src/Chorus.Rpc/Protos/m3.proto by the FIX-WEB-05 contract generator
// (tests/Chorus.Engine.Tests/AdminTsContract.cs). Do not edit by hand: regenerate with
// CHORUS_REGEN_ADMIN_TS=1 dotnet test --filter "FullyQualifiedName~RedesignWeb05AdminClientContractTests"
// and commit the result; the CI gate re-renders from the descriptors and refuses drift.

export interface AdminBindingProto {
  bindingId: string;
  gameId: string;
  generation: number;
  state: string;
  currentRoomId: string;
  createdUnixMs: number;
}

export interface AdminBumpRoomEpochReply {
  operationId: string;
  roomId: string;
  ownerInstanceId: string;
  oldEpoch: number;
  newEpoch: number;
  ownershipStatus: string;
  routeSyncStatus: string;
  duplicate: boolean;
  tenantId: string;
  gameId: string;
}

export interface AdminBumpRoomEpochRequest {
  requestId: string;
  operator: string;
  reason: string;
  tenantId: string;
  gameId: string;
  roomId: string;
  expectedEpoch: number;
  targetEpoch: number;
}

export interface AdminDispatchRoomCommandReply {
  operationId: string;
  roomId: string;
  ownerInstanceId: string;
  epoch: number;
  stage: string;
  duplicate: boolean;
  note: string;
  tenantId: string;
  gameId: string;
}

export interface AdminDispatchRoomCommandRequest {
  requestId: string;
  operator: string;
  reason: string;
  tenantId: string;
  gameId: string;
  roomId: string;
  expectedEpoch: number;
  participantId: string;
  payloadJson: string;
}

export interface AdminDrainProcessReply {
  processInstanceId: string;
  found: boolean;
  lifecycle: string;
  duplicate: boolean;
}

export interface AdminDrainProcessRequest {
  requestId: string;
  operator: string;
  reason: string;
  processInstanceId: string;
}

export interface AdminGetOperationStatusReply {
  found: boolean;
  stage: string;
  appliedTick: number;
  platform: string;
  note: string;
}

export interface AdminGetOperationStatusRequest {
  operationId: string;
  tenantId: string;
  gameId: string;
  roomId: string;
}

export interface AdminGetRoomEpochReply {
  found: boolean;
  epoch: number;
  ownerInstanceId: string;
  status: string;
  tenantId: string;
  gameId: string;
  roomId: string;
}

export interface AdminGetRoomEpochRequest {
  requestId: string;
  operator: string;
  reason: string;
  tenantId: string;
  gameId: string;
  roomId: string;
}

export interface AdminLeaderboardAdjustReply {
  score: number;
  duplicate: boolean;
  readVersionUnixMs: number;
  readVersion: string;
}

export interface AdminLeaderboardAdjustRequest {
  requestId: string;
  operator: string;
  reason: string;
  tenantId: string;
  gameId: string;
  board: string;
  periodId: string;
  scopeId: string;
  subject: number;
  subjectId: string;
  delta: number;
  expectedVersion: string;
}

export interface AdminLeaderboardAroundReply {
  found: boolean;
  entries: AdminLeaderboardRankedEntryProto[];
  readVersionUnixMs: number;
  readVersion: string;
}

export interface AdminLeaderboardAroundRequest {
  tenantId: string;
  gameId: string;
  board: string;
  periodId: string;
  scopeId: string;
  subject: number;
  subjectId: string;
  window: number;
}

export interface AdminLeaderboardEntryProto {
  subjectId: string;
  score: number;
}

export interface AdminLeaderboardRankReply {
  found: boolean;
  rank: number;
  score: number;
  readVersionUnixMs: number;
  readVersion: string;
}

export interface AdminLeaderboardRankRequest {
  tenantId: string;
  gameId: string;
  board: string;
  periodId: string;
  scopeId: string;
  subject: number;
  subjectId: string;
}

export interface AdminLeaderboardRankedEntryProto {
  rank: number;
  subjectId: string;
  score: number;
}

export interface AdminLeaderboardRebuildReply {
  entries: number;
  readVersionUnixMs: number;
  readVersion: string;
}

export interface AdminLeaderboardRebuildRequest {
  requestId: string;
  operator: string;
  reason: string;
  tenantId: string;
  gameId: string;
  board: string;
  periodId: string;
  scopeId: string;
  subject: number;
}

export interface AdminLeaderboardResetReply {
  duplicate: boolean;
  readVersionUnixMs: number;
  readVersion: string;
}

export interface AdminLeaderboardResetRequest {
  requestId: string;
  operator: string;
  reason: string;
  tenantId: string;
  gameId: string;
  board: string;
  periodId: string;
  scopeId: string;
  subject: number;
  subjectId: string;
  expectedVersion: string;
}

export interface AdminLeaderboardTopReply {
  entries: AdminLeaderboardEntryProto[];
  readVersionUnixMs: number;
  hasMore: boolean;
  nextPageToken: string;
  readVersion: string;
}

export interface AdminLeaderboardTopRequest {
  tenantId: string;
  gameId: string;
  board: string;
  periodId: string;
  scopeId: string;
  subject: number;
  maxEntries: number;
  pageToken: string;
}

export interface AdminParticipantProto {
  participantId: string;
  bindingId: string;
  role: string;
  state: string;
}

export interface AdminQueueGateReply {
  partitionKey: string;
  suspended: boolean;
  duplicate: boolean;
}

export interface AdminQueueGateRequest {
  requestId: string;
  operator: string;
  reason: string;
  partitionKey: string;
}

export interface AdminRoomRecoveryReply {
  ownerInstanceId: string;
  epoch: number;
  recovered: boolean;
  status: string;
  lastTick: number;
  duplicate: boolean;
}

export interface AdminRoomRecoveryRequest {
  requestId: string;
  operator: string;
  reason: string;
  tenantId: string;
  gameId: string;
  roomId: string;
  toInstanceId: string;
  tickMs: number;
}

export interface AdminRoomSnapshotReply {
  found: boolean;
  tenantId: string;
  gameId: string;
  status: string;
  epoch: number;
  lastTick: number;
  stateVersion: number;
  ownershipPresent: boolean;
  ownerInstanceId: string;
  ownershipEpoch: number;
  ownershipStatus: string;
  participants: AdminParticipantProto[];
  resultVersion: number;
  finalFrame: number;
  grantsTotal: number;
  grantsGranted: number;
  settleStatus: string;
  settleAttempts: number;
  settleLastError: string;
  settleNextAttemptUnixMs: number;
  settleOwnerInstanceId: string;
  releaseBlockedReason: string;
  releaseBlockedSinceUnixMs: number;
  releaseBlockedTotalMs: number;
}

export interface AdminRoomSnapshotRequest {
  roomId: string;
}

export interface AdminSettlementRetryReply {
  roomId: string;
  resultVersion: number;
  grantsTotal: number;
  grantsGranted: number;
  grantsPending: number;
  duplicate: boolean;
}

export interface AdminSettlementRetryRequest {
  requestId: string;
  operator: string;
  reason: string;
  roomId: string;
}

export interface AdminStreamerSnapshotReply {
  found: boolean;
  streamerId: string;
  banState: string;
  currentSessionId: string;
  sessionVersion: number;
  bindings: AdminBindingProto[];
  ticketPresent: boolean;
  ticket?: TicketProto;
  ownershipPresent: boolean;
  ownerInstanceId: string;
  ownershipEpoch: number;
  ownershipStatus: string;
  tasks: AdminTaskProto[];
}

export interface AdminStreamerSnapshotRequest {
  tenantId: string;
  streamerId: string;
}

export interface AdminTaskProto {
  taskId: string;
  operationId: string;
  status: string;
  attemptCount: number;
  taskEpoch: number;
}

export interface AdminTaskReconcileReply {
  taskId: string;
  status: string;
  taskEpoch: number;
  duplicate: boolean;
}

export interface AdminTaskReconcileRequest {
  requestId: string;
  operator: string;
  reason: string;
  taskId: string;
}

export interface AdminTraceEffectProto {
  effectId: string;
  kind: string;
  status: string;
  payloadJson: string;
}

export interface AdminTraceEventProto {
  sequence: number;
  eventId: string;
  disposition: string;
  bindingId: string;
  receivedUnixMs: number;
  payloadJson: string;
}

export interface AdminTraceFrameProto {
  tick: number;
  commitId: string;
  inputRefsJson: string;
  effectsJson: string;
}

export interface AdminTraceGrantProto {
  playerId: string;
  grantKey: string;
  status: string;
  operationId: string;
}

export interface AdminTraceLedgerProto {
  operationId: string;
  lineId: string;
  playerId: string;
  assetId: string;
  delta: number;
  reason: string;
  resultingBalance: number;
  committedUnixMs: number;
}

export interface AdminTraceRoundReply {
  found: boolean;
  status: string;
  epoch: number;
  lastTick: number;
  events: AdminTraceEventProto[];
  frames: AdminTraceFrameProto[];
  effects: AdminTraceEffectProto[];
  resultVersion: number;
  resultJson: string;
  grants: AdminTraceGrantProto[];
  ledger: AdminTraceLedgerProto[];
}

export interface AdminTraceRoundRequest {
  roomId: string;
}

export interface AdminWhoAmIReply {
  operatorId: string;
  role: string;
  tenants: string[];
  games: string[];
  enabled: boolean;
}

export interface Empty {
}

export interface TicketProto {
  ticketId: string;
  bindingId: string;
  state: string;
  version: number;
  partitionKey: string;
  expiresUnixMs: number;
  reservationId: string;
  streamerId: string;
  tenantId: string;
  gameId: string;
}

// The route table, read from the google.api.http annotations.
export const adminRoutes = {
  whoAmI: '/admin/api/v1/who-am-i',
  getStreamerSnapshot: '/admin/api/v1/get-streamer-snapshot',
  getRoomSnapshot: '/admin/api/v1/get-room-snapshot',
  traceRound: '/admin/api/v1/trace-round',
  suspendMatchQueue: '/admin/api/v1/suspend-match-queue',
  resumeMatchQueue: '/admin/api/v1/resume-match-queue',
  retryRoomRecovery: '/admin/api/v1/retry-room-recovery',
  drainProcess: '/admin/api/v1/drain-process',
  retrySettlement: '/admin/api/v1/retry-settlement',
  reconcilePlatformTask: '/admin/api/v1/reconcile-platform-task',
  getRoomEpoch: '/admin/api/v1/get-room-epoch',
  bumpRoomEpoch: '/admin/api/v1/bump-room-epoch',
  dispatchRoomCommand: '/admin/api/v1/dispatch-room-command',
  getOperationStatus: '/admin/api/v1/get-operation-status',
  getLeaderboardTop: '/admin/api/v1/get-leaderboard-top',
  getLeaderboardRank: '/admin/api/v1/get-leaderboard-rank',
  getLeaderboardAround: '/admin/api/v1/get-leaderboard-around',
  adjustLeaderboardEntry: '/admin/api/v1/adjust-leaderboard-entry',
  resetLeaderboardEntry: '/admin/api/v1/reset-leaderboard-entry',
  rebuildLeaderboard: '/admin/api/v1/rebuild-leaderboard',
} as const;

export type AdminRouteName = keyof typeof adminRoutes;

// The typed client over the route table; the transport is injected so the
// browser core owns the credential header and the error mapping.
export class AdminClient {
  constructor(private readonly call: <Req, Rep>(path: string, request: Req) => Promise<Rep>) {}
  whoAmI(request: Empty): Promise<AdminWhoAmIReply> {
    return this.call<Empty, AdminWhoAmIReply>(
      adminRoutes.whoAmI, request);
  }
  getStreamerSnapshot(request: AdminStreamerSnapshotRequest): Promise<AdminStreamerSnapshotReply> {
    return this.call<AdminStreamerSnapshotRequest, AdminStreamerSnapshotReply>(
      adminRoutes.getStreamerSnapshot, request);
  }
  getRoomSnapshot(request: AdminRoomSnapshotRequest): Promise<AdminRoomSnapshotReply> {
    return this.call<AdminRoomSnapshotRequest, AdminRoomSnapshotReply>(
      adminRoutes.getRoomSnapshot, request);
  }
  traceRound(request: AdminTraceRoundRequest): Promise<AdminTraceRoundReply> {
    return this.call<AdminTraceRoundRequest, AdminTraceRoundReply>(
      adminRoutes.traceRound, request);
  }
  suspendMatchQueue(request: AdminQueueGateRequest): Promise<AdminQueueGateReply> {
    return this.call<AdminQueueGateRequest, AdminQueueGateReply>(
      adminRoutes.suspendMatchQueue, request);
  }
  resumeMatchQueue(request: AdminQueueGateRequest): Promise<AdminQueueGateReply> {
    return this.call<AdminQueueGateRequest, AdminQueueGateReply>(
      adminRoutes.resumeMatchQueue, request);
  }
  retryRoomRecovery(request: AdminRoomRecoveryRequest): Promise<AdminRoomRecoveryReply> {
    return this.call<AdminRoomRecoveryRequest, AdminRoomRecoveryReply>(
      adminRoutes.retryRoomRecovery, request);
  }
  drainProcess(request: AdminDrainProcessRequest): Promise<AdminDrainProcessReply> {
    return this.call<AdminDrainProcessRequest, AdminDrainProcessReply>(
      adminRoutes.drainProcess, request);
  }
  retrySettlement(request: AdminSettlementRetryRequest): Promise<AdminSettlementRetryReply> {
    return this.call<AdminSettlementRetryRequest, AdminSettlementRetryReply>(
      adminRoutes.retrySettlement, request);
  }
  reconcilePlatformTask(request: AdminTaskReconcileRequest): Promise<AdminTaskReconcileReply> {
    return this.call<AdminTaskReconcileRequest, AdminTaskReconcileReply>(
      adminRoutes.reconcilePlatformTask, request);
  }
  getRoomEpoch(request: AdminGetRoomEpochRequest): Promise<AdminGetRoomEpochReply> {
    return this.call<AdminGetRoomEpochRequest, AdminGetRoomEpochReply>(
      adminRoutes.getRoomEpoch, request);
  }
  bumpRoomEpoch(request: AdminBumpRoomEpochRequest): Promise<AdminBumpRoomEpochReply> {
    return this.call<AdminBumpRoomEpochRequest, AdminBumpRoomEpochReply>(
      adminRoutes.bumpRoomEpoch, request);
  }
  dispatchRoomCommand(request: AdminDispatchRoomCommandRequest): Promise<AdminDispatchRoomCommandReply> {
    return this.call<AdminDispatchRoomCommandRequest, AdminDispatchRoomCommandReply>(
      adminRoutes.dispatchRoomCommand, request);
  }
  getOperationStatus(request: AdminGetOperationStatusRequest): Promise<AdminGetOperationStatusReply> {
    return this.call<AdminGetOperationStatusRequest, AdminGetOperationStatusReply>(
      adminRoutes.getOperationStatus, request);
  }
  getLeaderboardTop(request: AdminLeaderboardTopRequest): Promise<AdminLeaderboardTopReply> {
    return this.call<AdminLeaderboardTopRequest, AdminLeaderboardTopReply>(
      adminRoutes.getLeaderboardTop, request);
  }
  getLeaderboardRank(request: AdminLeaderboardRankRequest): Promise<AdminLeaderboardRankReply> {
    return this.call<AdminLeaderboardRankRequest, AdminLeaderboardRankReply>(
      adminRoutes.getLeaderboardRank, request);
  }
  getLeaderboardAround(request: AdminLeaderboardAroundRequest): Promise<AdminLeaderboardAroundReply> {
    return this.call<AdminLeaderboardAroundRequest, AdminLeaderboardAroundReply>(
      adminRoutes.getLeaderboardAround, request);
  }
  adjustLeaderboardEntry(request: AdminLeaderboardAdjustRequest): Promise<AdminLeaderboardAdjustReply> {
    return this.call<AdminLeaderboardAdjustRequest, AdminLeaderboardAdjustReply>(
      adminRoutes.adjustLeaderboardEntry, request);
  }
  resetLeaderboardEntry(request: AdminLeaderboardResetRequest): Promise<AdminLeaderboardResetReply> {
    return this.call<AdminLeaderboardResetRequest, AdminLeaderboardResetReply>(
      adminRoutes.resetLeaderboardEntry, request);
  }
  rebuildLeaderboard(request: AdminLeaderboardRebuildRequest): Promise<AdminLeaderboardRebuildReply> {
    return this.call<AdminLeaderboardRebuildRequest, AdminLeaderboardRebuildReply>(
      adminRoutes.rebuildLeaderboard, request);
  }
}
