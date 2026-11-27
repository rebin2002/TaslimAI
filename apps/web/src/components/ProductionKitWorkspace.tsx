"use client";
/* eslint-disable @next/next/no-img-element -- previews use authenticated Asset URLs. */
/* eslint-disable react-hooks/set-state-in-effect -- loaders synchronize persisted read models. */
/* eslint-disable react-hooks/exhaustive-deps -- loader identity follows the project route. */
import Link from "next/link";
import { useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import { AlertTriangle, ArrowUpRight, CheckCircle2, Image as ImageIcon, LockKeyhole, MapPinned, Package, ShieldCheck, Sparkles, UsersRound } from "lucide-react";
import { api, type MovieCast, type MovieCharacter, type MovieProp, type MovieWorldWorkspace } from "@/lib/api";
import { assetFileUrl } from "@/lib/apiBase";
import { useLocale } from "@/components/LocaleProvider";

type KitRoom = "characters" | "locations" | "props";
type KitState = "ready" | "needs-reference" | "needs-approval";
type KitRecord = {
  id: string;
  room: KitRoom;
  name: string;
  description: string;
  referenceAssetId: string | null;
  referenceCount: number;
  lockCount: number;
  state: KitState;
  role: string | null;
};

type RoomDefinition = { id: KitRoom; label: string; hint: string; icon: typeof UsersRound };
const rooms: RoomDefinition[] = [
  { id: "characters", label: "characters", hint: "identity", icon: UsersRound },
  { id: "locations", label: "locations", hint: "locationsHint", icon: MapPinned },
  { id: "props", label: "props", hint: "propsHint", icon: Package },
];

type WorkspaceState = {
  projectId: string;
  cast: MovieCast | null;
  world: MovieWorldWorkspace | null;
  characterDetail: MovieCharacter | null;
  room: KitRoom;
  selectedId: string | null;
  loading: boolean;
  busy: string;
  error: string;
};

function emptyWorkspace(projectId: string): WorkspaceState {
  return { projectId, cast: null, world: null, characterDetail: null, room: "characters", selectedId: null, loading: true, busy: "", error: "" };
}

function recordState(referenceCount: number, lockCount: number): KitState {
  if (referenceCount === 0) return "needs-reference";
  if (lockCount === 0) return "needs-approval";
  return "ready";
}

function buildKitRecords(room: KitRoom, cast: MovieCast | null, world: MovieWorldWorkspace | null): KitRecord[] {
  if (room === "characters") {
    return (cast?.characters ?? []).map((character) => ({
      id: character.id,
      room,
      name: character.name,
      description: character.description,
      role: character.role,
      referenceAssetId: character.referenceAssetId,
      referenceCount: character.referenceAssetCount,
      lockCount: character.lockedFactCount,
      state: recordState(character.referenceAssetCount, character.lockedFactCount),
    }));
  }
  const records = room === "locations" ? world?.world.locations ?? [] : world?.world.props ?? [];
  return records.map((record) => {
    const referenceAssetId = record.referenceAssetId;
    const lockCount = world?.world.locks.filter((lock) => lock.entityId === record.id && lock.entityType === (room === "locations" ? "location" : "prop")).length ?? 0;
    return {
      id: record.id,
      room,
      name: record.name,
      description: record.description,
      role: room === "locations" ? "Physical location" : (record as MovieProp).category || "Continuity prop",
      referenceAssetId,
      referenceCount: referenceAssetId ? 1 : 0,
      lockCount,
      state: recordState(referenceAssetId ? 1 : 0, lockCount),
    };
  });
}

function stateLabel(state: KitState) {
  return state === "ready" ? "Ready" : state === "needs-reference" ? "Needs reference" : "Needs approval";
}

function stateDetail(state: KitState) {
  return state === "ready" ? "Reference and identity approved" : state === "needs-reference" ? "Attach a visual anchor before production" : "Review and lock the identity before production";
}

function recordIcon(room: KitRoom) {
  return room === "characters" ? UsersRound : room === "locations" ? MapPinned : Package;
}

function KitReferencePreview({ assetId, name, advertised, world }: {
  assetId: string | null;
  name: string;
  advertised: boolean | undefined;
  world: MovieWorldWorkspace;
}) {
  const { t } = useLocale();
  const [result, setResult] = useState<{ world: MovieWorldWorkspace; available: boolean } | null>(null);

  // World rooms carry readiness in their projection. Character-only references need the
  // authorized asset detail. A fresh world read also revalidates the same selected reference.
  useEffect(() => {
    if (!assetId || advertised !== undefined) return;
    let active = true;
    void api.getAsset(assetId).then(
      (asset) => {
        if (active) setResult({ world, available: asset.id === assetId && asset.hasFile && asset.canPreview });
      },
      () => {
        if (active) setResult({ world, available: false });
      },
    );
    return () => { active = false; };
  }, [assetId, advertised, world]);

  const available = result?.world === world ? result.available : advertised;
  if (assetId && available) {
    return <div className="movie-kit-reference-preview">
      <img src={assetFileUrl(assetId, true)} alt={`${name} reference`}
        onError={() => setResult({ world, available: false })} />
    </div>;
  }

  const label = !assetId ? "movieBody.kit.referenceNeeded" : available === undefined ? "movieBody.loading" : "assets.fileUnavailable";
  return <div className="movie-kit-reference-empty"><ImageIcon size={21} /><strong>{t(label)}</strong><span>{t("movieBody.world.addFirst")}</span></div>;
}

export function ProductionKitWorkspace({ projectId }: { projectId: string }) {
  const { t } = useLocale();
  const [workspaceState, setWorkspaceState] = useState<WorkspaceState>(() => emptyWorkspace(projectId));
  const workspace = workspaceState.projectId === projectId ? workspaceState : emptyWorkspace(projectId);
  const { cast, world, characterDetail, room, selectedId, loading, busy, error } = workspace;
  function updateWorkspace(requestProjectId: string, patch: Partial<Omit<WorkspaceState, "projectId">>) {
    setWorkspaceState((current) => current.projectId === requestProjectId ? { ...current, ...patch } : current);
  }
  const loadGeneration = useRef(0);
  const selectionGeneration = useRef(0);
  const currentProjectId = useRef(projectId);

  async function load(nextRoom = room, nextId: string | null = selectedId) {
    const requestProjectId = projectId;
    if (currentProjectId.current !== requestProjectId) return;
    const generation = ++loadGeneration.current;
    const selectionAtStart = selectionGeneration.current;
    const isCurrentRequest = () => generation === loadGeneration.current && selectionAtStart === selectionGeneration.current && currentProjectId.current === requestProjectId;
    updateWorkspace(requestProjectId, { loading: true, error: "" });
    try {
      const [nextCast, nextWorld] = await Promise.all([api.getMovieCast(requestProjectId), api.getMovieWorld(requestProjectId)]);
      if (!isCurrentRequest()) return;
      const nextRecords = buildKitRecords(nextRoom, nextCast, nextWorld);
      const resolvedId = nextId && nextRecords.some((record) => record.id === nextId) ? nextId : nextRecords[0]?.id ?? null;
      updateWorkspace(requestProjectId, { cast: nextCast, world: nextWorld, room: nextRoom, selectedId: resolvedId, characterDetail: null });
      if (nextRoom === "characters" && resolvedId) {
        const detail = await api.getMovieCharacterDetail(resolvedId);
        if (!isCurrentRequest()) return;
        if (detail.character.id !== resolvedId) {
          updateWorkspace(requestProjectId, { error: "The selected character could not be loaded." });
          return;
        }
        updateWorkspace(requestProjectId, { characterDetail: detail.character });
      }
    } catch (cause) {
      if (!isCurrentRequest()) return;
      updateWorkspace(requestProjectId, { error: cause instanceof Error ? cause.message : "The Production Kit could not be loaded." });
    } finally {
      if (generation === loadGeneration.current && currentProjectId.current === requestProjectId) updateWorkspace(requestProjectId, { loading: false });
    }
  }

  useLayoutEffect(() => {
    currentProjectId.current = projectId;
    loadGeneration.current += 1;
    selectionGeneration.current += 1;
    setWorkspaceState(emptyWorkspace(projectId));
    return () => {
      loadGeneration.current += 1;
      selectionGeneration.current += 1;
    };
  }, [projectId]);

  useEffect(() => {
    void load("characters", null);
  }, [projectId]);

  const records = useMemo(() => buildKitRecords(room, cast, world), [cast, room, world]);
  const selected = records.find((record) => record.id === selectedId) ?? records[0] ?? null;
  const selectedCharacterDetail = room === "characters" && characterDetail?.id === selected?.id ? characterDetail : null;
  const selectedLocation = room === "locations" ? world?.world.locations.find((item) => item.id === selected?.id) ?? null : null;
  const selectedProp = room === "props" ? world?.world.props.find((item) => item.id === selected?.id) ?? null : null;
  const selectedAssetId = selectedCharacterDetail?.referenceAssetId ?? selected?.referenceAssetId ?? null;
  const selectedAsset = world?.assets.find((asset) => asset.id === selectedAssetId) ?? null;
  const selectedReferenceIsAdvertised = selectedAsset
    ? selectedAsset.hasFile && selectedAsset.canPreview
    : room === "characters" ? undefined : false;
  const selectedLocks = room === "characters"
    ? selectedCharacterDetail?.continuityLocks ?? []
    : world?.world.locks.filter((lock) => lock.entityId === selected?.id) ?? [];
  const imageAssets = (world?.assets ?? []).filter((asset) => asset.canPreview && (asset.assetType === "image" || asset.mimeType?.startsWith("image/") === true));
  const counts = useMemo(() => {
    const all = [...buildKitRecords("characters", cast, world), ...buildKitRecords("locations", cast, world), ...buildKitRecords("props", cast, world)];
    return { total: all.length, ready: all.filter((item) => item.state === "ready").length, references: all.filter((item) => item.referenceCount > 0).length, approvals: all.filter((item) => item.lockCount > 0).length, missing: all.filter((item) => item.state === "needs-reference").length };
  }, [cast, world]);

  async function selectRoom(nextRoom: KitRoom) {
    const selection = ++selectionGeneration.current;
    loadGeneration.current += 1;
    const requestProjectId = projectId;
    const isCurrentSelection = () => selection === selectionGeneration.current && currentProjectId.current === requestProjectId;
    const nextRecords = buildKitRecords(nextRoom, cast, world);
    const nextId = nextRecords[0]?.id ?? null;
    updateWorkspace(requestProjectId, { room: nextRoom, selectedId: nextId, characterDetail: null, loading: false, busy: "", error: "" });
    if (nextRoom === "characters" && nextId) {
      try {
        const detail = await api.getMovieCharacterDetail(nextId);
        if (!isCurrentSelection()) return;
        if (detail.character.id !== nextId) {
          updateWorkspace(requestProjectId, { error: "The selected character could not be loaded." });
          return;
        }
        updateWorkspace(requestProjectId, { characterDetail: detail.character });
      } catch (cause) {
        if (!isCurrentSelection()) return;
        updateWorkspace(requestProjectId, { error: cause instanceof Error ? cause.message : "The character could not be loaded." });
      }
    }
  }

  async function selectRecord(id: string) {
    const selection = ++selectionGeneration.current;
    loadGeneration.current += 1;
    const requestProjectId = projectId;
    const isCurrentSelection = () => selection === selectionGeneration.current && currentProjectId.current === requestProjectId;
    updateWorkspace(requestProjectId, { selectedId: id, characterDetail: null, loading: false, busy: "", error: "" });
    if (room !== "characters") return;
    try {
      const detail = await api.getMovieCharacterDetail(id);
      if (!isCurrentSelection()) return;
      if (detail.character.id !== id) {
        updateWorkspace(requestProjectId, { error: "The selected character could not be loaded." });
        return;
      }
      updateWorkspace(requestProjectId, { characterDetail: detail.character });
    } catch (cause) {
      if (!isCurrentSelection()) return;
      updateWorkspace(requestProjectId, { error: cause instanceof Error ? cause.message : "The character could not be loaded." });
    }
  }

  async function attachReference(assetId: string) {
    const target = selected;
    if (!target || !assetId) return;
    const requestProjectId = projectId;
    const requestRoom = room;
    const selection = selectionGeneration.current;
    const isCurrentAction = () => currentProjectId.current === requestProjectId && selectionGeneration.current === selection;
    if (!isCurrentAction()) return;
    updateWorkspace(requestProjectId, { busy: "reference", error: "" });
    try {
      if (requestRoom === "characters") {
        let source = selectedCharacterDetail;
        if (!source) {
          const detail = await api.getMovieCharacterDetail(target.id);
          if (!isCurrentAction()) return;
          if (detail.character.id !== target.id) throw new Error("The selected character could not be loaded.");
          source = detail.character;
        }
        if (!isCurrentAction() || source.id !== target.id) return;
        const referenceAssetIds = Array.from(new Set([...source.referenceAssetIds, assetId]));
        await api.updateMovieCharacter(source.id, { name: source.name, role: source.role, description: source.description, appearance: source.appearance, physicalDescription: source.physicalDescription, wardrobe: source.wardrobe, voiceReference: source.voiceReference, personalityAndStoryNotes: source.personalityAndStoryNotes, voiceAndPerformance: source.voiceAndPerformance, continuityNotes: source.continuityNotes, referenceAssetId: referenceAssetIds[0] ?? null, referenceAssetIds });
      } else if (requestRoom === "locations" && selectedLocation) {
        await api.updateMovieLocation(selectedLocation.id, { name: selectedLocation.name, description: selectedLocation.description, visualContinuityNotes: selectedLocation.visualContinuityNotes, referenceAssetId: assetId });
      } else if (requestRoom === "props" && selectedProp) {
        await api.updateMovieProp(selectedProp.id, { name: selectedProp.name, description: selectedProp.description, category: selectedProp.category, continuityNotes: selectedProp.continuityNotes, referenceAssetId: assetId });
      }
      if (!isCurrentAction()) return;
      await load(requestRoom, target.id);
    } catch (cause) {
      if (!isCurrentAction()) return;
      updateWorkspace(requestProjectId, { error: cause instanceof Error ? cause.message : "The reference could not be attached." });
    } finally {
      if (isCurrentAction()) updateWorkspace(requestProjectId, { busy: "" });
    }
  }

  async function approveIdentity() {
    const target = selected;
    if (!target || target.lockCount > 0 || !target.referenceCount) return;
    const requestProjectId = projectId;
    const requestRoom = room;
    const selection = selectionGeneration.current;
    const isCurrentAction = () => currentProjectId.current === requestProjectId && selectionGeneration.current === selection;
    if (!isCurrentAction()) return;
    updateWorkspace(requestProjectId, { busy: "approval", error: "" });
    try {
      if (requestRoom === "characters") {
        let source = selectedCharacterDetail;
        if (!source) {
          const detail = await api.getMovieCharacterDetail(target.id);
          if (!isCurrentAction()) return;
          if (detail.character.id !== target.id) throw new Error("The selected character could not be loaded.");
          source = detail.character;
        }
        if (!isCurrentAction() || source.id !== target.id) return;
        await api.lockMovieCharacterFact(source.id, { fieldKey: "name", lockedValue: source.name });
      } else {
        await api.addMovieContinuityLock(requestProjectId, { entityType: requestRoom === "locations" ? "location" : "prop", entityId: target.id, fieldName: "name", lockedValue: target.name, strength: "hard", reason: "Production Kit identity approved." });
      }
      if (!isCurrentAction()) return;
      await load(requestRoom, target.id);
    } catch (cause) {
      if (!isCurrentAction()) return;
      updateWorkspace(requestProjectId, { error: cause instanceof Error ? cause.message : "The identity approval could not be saved." });
    } finally {
      if (isCurrentAction()) updateWorkspace(requestProjectId, { busy: "" });
    }
  }

  if (loading && !world) return <div className="movie-kit-loading"><span className="loading-spinner" /><span>{t("movieBody.loading")}</span></div>;
  if (!world || !cast) return <div className="movie-kit-empty"><ShieldCheck size={22} /><strong>{t("movieBody.kit.referenceNeeded")}</strong><p>{error || t("movieBody.world.addFirst")}</p></div>;

  return <div className="movie-module-stack movie-kit-room">
    <section className="movie-kit-hero"><div><span className="movie-workspace-kicker">{t("movieBody.kit.productionMap")} · {t("movieBody.kit.anchorRooms")}</span><h3>{t("movieDeep.productionIdentity")}</h3><p>{t("movieDeep.assetLibraryReuse")}</p></div><div className="movie-kit-hero-stat"><strong>{counts.ready}/{counts.total || 0}</strong><span>{t("movieBody.kit.complete")}</span></div></section>
    <section className="movie-kit-readiness" aria-label={t("movieBody.kit.readiness")}><div className="movie-kit-readiness-heading"><div><span className="movie-workspace-kicker">{t("movieBody.kit.readiness")}</span><h4>{t("movieBody.kit.coverage")}</h4></div><span>{counts.total ? `${Math.round((counts.ready / counts.total) * 100)}% ${t("movieBody.kit.complete")}` : t("movieBody.noRecords")}</span></div><div className="movie-kit-progress"><span style={{ width: `${counts.total ? (counts.ready / counts.total) * 100 : 0}%` }} /></div><div className="movie-kit-readiness-stats"><span><strong>{counts.references}</strong> referenced</span><span><strong>{counts.approvals}</strong> approved</span><span className={counts.missing ? "is-alert" : ""}><strong>{counts.missing}</strong> missing reference</span></div></section>
    <div className="movie-kit-layout">
      <aside className="movie-kit-index"><div className="movie-kit-index-heading"><div><span className="movie-workspace-kicker">{t("movieBody.kit.productionMap")}</span><h4>{t("movieBody.kit.anchorRooms")}</h4></div><Sparkles size={16} /></div><div className="movie-kit-room-tabs" role="tablist" aria-label={t("movieBody.kit.anchorRooms")}>{rooms.map((item) => { const Icon = item.icon; const items = buildKitRecords(item.id, cast, world); return <button key={item.id} type="button" role="tab" aria-selected={room === item.id} className={room === item.id ? "is-active" : ""} onClick={() => void selectRoom(item.id)}><Icon size={15} /><span><strong>{item.label === "characters" ? t("movieModule.cast.label") : t(`movieBody.world.${item.label}`)}</strong><small>{item.hint === "identity" ? t("movieModule.cast.description") : t(`movieBody.world.${item.hint}`)}</small></span><em>{items.length}</em></button>; })}</div><div className="movie-kit-index-list">{records.length ? records.map((record, index) => { const Icon = recordIcon(record.room); return <button type="button" key={record.id} className={`movie-kit-record ${selected?.id === record.id ? "is-selected" : ""}`} onClick={() => void selectRecord(record.id)}><span className="movie-kit-record-number">{String(index + 1).padStart(2, "0")}</span><span className="movie-kit-record-icon"><Icon size={14} /></span><span className="movie-kit-record-copy"><strong>{record.name}</strong><small>{record.role || t("movieDeep.productionIdentity")}</small></span><span className={`movie-kit-state-dot is-${record.state}`} aria-label={stateLabel(record.state)} /></button>; }) : <div className="movie-kit-list-empty"><Package size={18} /><strong>{t("movieBody.noRecords")}</strong><p>{t("movieBody.world.addFirst")}</p></div>}</div><Link className="movie-kit-open-room" href={`/create/movie/${projectId}/${room === "characters" ? "cast" : "world"}`}>{t("movieBody.kit.openRoom")} {room} room <ArrowUpRight size={13} /></Link></aside>
      <main className="movie-kit-inspector">{selected ? <><header className="movie-kit-inspector-header"><div><span className="movie-workspace-kicker">{selected.role || t("movieDeep.productionIdentity")}</span><h4>{selected.name}</h4><p>{selected.description}</p></div><span className={`movie-kit-status-badge is-${selected.state}`}>{selected.state === "ready" ? <CheckCircle2 size={12} /> : <AlertTriangle size={12} />}{stateLabel(selected.state)}</span></header><div className="movie-kit-inspector-grid"><section className="movie-kit-reference-card"><div className="movie-kit-card-heading"><div><span className="movie-inspector-label">{t("movieBody.cast.referenceAnchor")}</span><h5>{selectedAsset?.name || t("movieBody.kit.referenceNeeded")}</h5></div><ImageIcon size={16} /></div><KitReferencePreview
        key={`${projectId}:${room}:${selected.id}:${selectedAssetId ?? ""}`}
        assetId={selectedAssetId} name={selected.name} advertised={selectedReferenceIsAdvertised} world={world}
      /><div className="movie-kit-reference-actions"><label><span>{selectedAssetId ? t("movieBody.edit") : t("movieDeep.assetLibraryReuse")}</span><select value="" onChange={(event) => void attachReference(event.target.value)} disabled={!imageAssets.length || busy === "reference"}><option value="">{imageAssets.length ? t("movieBody.world.openLibrary") : t("movieBody.noRecords")}</option>{imageAssets.map((asset) => <option key={asset.id} value={asset.id}>{asset.name}</option>)}</select></label><Link href={`/assets?projectId=${encodeURIComponent(world.projectId ?? projectId)}&assetType=image`}><ArrowUpRight size={13} /> {t("movieBody.world.openLibrary")}</Link></div></section><section className="movie-kit-approval-card"><div className="movie-kit-card-heading"><div><span className="movie-inspector-label">{t("movieBody.approve")}</span><h5>{selected.lockCount ? t("movieBody.kit.identityApproved") : t("movieBody.kit.identityNeedsApproval")}</h5></div><LockKeyhole size={16} /></div><div className={`movie-kit-lock-state ${selected.lockCount ? "is-locked" : ""}`}>{selected.lockCount ? <><CheckCircle2 size={16} /><div><strong>{t("movieBody.kit.protectedContinuity")}</strong><span>{selected.lockCount} approved lock{selected.lockCount === 1 ? "" : "s"} keep this identity stable.</span></div></> : <><AlertTriangle size={16} /><div><strong>{t("movieBody.kit.notLocked")}</strong><span>{t("movieBody.kit.referenceNeeded")}</span></div></>}</div>{selectedLocks.length ? <div className="movie-kit-lock-list">{selectedLocks.slice(0, 4).map((lock) => <div key={lock.id}><LockKeyhole size={11} /><strong>{"fieldKey" in lock ? lock.fieldKey : lock.fieldName}</strong><span>{"lockedValue" in lock ? lock.lockedValue : "Approved continuity value"}</span></div>)}</div> : null}<button type="button" className="movie-workspace-button is-primary movie-kit-approve" onClick={() => void approveIdentity()} disabled={selected.state !== "needs-approval" || busy === "approval"}><ShieldCheck size={14} />{busy === "approval" ? t("movieBody.loading") : t("movieBody.kit.approveIdentity")}</button><p className="movie-kit-approval-note">{t("movieBody.kit.approvalNote")}</p></section></div><section className="movie-kit-handoff"><div><span className="movie-workspace-kicker">{t("movieDeep.sceneShotUsage")}</span><h5>{stateDetail(selected.state)}</h5><p>{selected.state === "ready" ? "This record can be carried into shot planning without re-deciding its identity." : selected.state === "needs-reference" ? "Attach a real image from the private Asset Library, then return here to approve the identity." : "The reference is present. Approve the identity when the creative team agrees it is canonical."}</p></div><div className="movie-kit-handoff-meta"><span><strong>{selected.referenceCount}</strong> reference{selected.referenceCount === 1 ? "" : "s"}</span><span><strong>{selected.lockCount}</strong> lock{selected.lockCount === 1 ? "" : "s"}</span></div></section></> : <div className="movie-kit-no-selection"><ShieldCheck size={24} /><strong>{t("movieBody.kit.chooseRecord")}</strong><p>{t("movieBody.kit.coverage")}</p></div>}{error && <div className="movie-workspace-error-inline" role="alert"><span className="movie-error-icon">!</span><span>{error}</span></div>}</main>
    </div>
  </div>;
}

export function kitRecordForTest(room: KitRoom, cast: MovieCast | null, world: MovieWorldWorkspace | null) {
  return buildKitRecords(room, cast, world);
}
