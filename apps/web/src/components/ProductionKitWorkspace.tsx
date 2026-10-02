"use client";
/* eslint-disable @next/next/no-img-element -- previews use authenticated Asset URLs. */
/* eslint-disable react-hooks/set-state-in-effect -- loaders synchronize persisted read models. */
/* eslint-disable react-hooks/exhaustive-deps -- loader identity follows the project route. */
import Link from "next/link";
import { useEffect, useMemo, useState } from "react";
import { AlertTriangle, ArrowUpRight, CheckCircle2, Image as ImageIcon, LockKeyhole, MapPinned, Package, ShieldCheck, Sparkles, UsersRound } from "lucide-react";
import { useLocale } from "@/components/LocaleProvider";
import { api, type MovieCast, type MovieCharacter, type MovieProp, type MovieWorldWorkspace } from "@/lib/api";
import { assetFileUrl } from "@/lib/apiBase";

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
  { id: "characters", label: "Characters", hint: "Identity and performance", icon: UsersRound },
  { id: "locations", label: "Locations", hint: "Places with memory", icon: MapPinned },
  { id: "props", label: "Props", hint: "Objects that carry continuity", icon: Package },
];

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

export function ProductionKitWorkspace({ projectId }: { projectId: string }) {
  const { t } = useLocale();
  const [cast, setCast] = useState<MovieCast | null>(null);
  const [world, setWorld] = useState<MovieWorldWorkspace | null>(null);
  const [characterDetail, setCharacterDetail] = useState<MovieCharacter | null>(null);
  const [room, setRoom] = useState<KitRoom>("characters");
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState("");
  const [error, setError] = useState("");

  async function load(nextRoom = room, nextId: string | null = selectedId) {
    setLoading(true);
    setError("");
    try {
      const [nextCast, nextWorld] = await Promise.all([api.getMovieCast(projectId), api.getMovieWorld(projectId)]);
      setCast(nextCast);
      setWorld(nextWorld);
      const nextRecords = buildKitRecords(nextRoom, nextCast, nextWorld);
      const resolvedId = nextId && nextRecords.some((record) => record.id === nextId) ? nextId : nextRecords[0]?.id ?? null;
      setSelectedId(resolvedId);
      if (nextRoom === "characters" && resolvedId) {
        const detail = await api.getMovieCharacterDetail(resolvedId);
        setCharacterDetail(detail.character);
      } else {
        setCharacterDetail(null);
      }
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "The Production Kit could not be loaded.");
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => {
    void load("characters", null);
  }, [projectId]);

  const records = useMemo(() => buildKitRecords(room, cast, world), [cast, room, world]);
  const selected = records.find((record) => record.id === selectedId) ?? records[0] ?? null;
  const selectedLocation = room === "locations" ? world?.world.locations.find((item) => item.id === selected?.id) ?? null : null;
  const selectedProp = room === "props" ? world?.world.props.find((item) => item.id === selected?.id) ?? null : null;
  const selectedAssetId = room === "characters" ? characterDetail?.referenceAssetId ?? selected?.referenceAssetId ?? null : selected?.referenceAssetId ?? null;
  const selectedAsset = world?.assets.find((asset) => asset.id === selectedAssetId) ?? null;
  const selectedLocks = room === "characters"
    ? characterDetail?.continuityLocks ?? []
    : world?.world.locks.filter((lock) => lock.entityId === selected?.id) ?? [];
  const imageAssets = (world?.assets ?? []).filter((asset) => asset.canPreview && (asset.assetType === "image" || asset.mimeType?.startsWith("image/") === true));
  const counts = useMemo(() => {
    const all = [...buildKitRecords("characters", cast, world), ...buildKitRecords("locations", cast, world), ...buildKitRecords("props", cast, world)];
    return { total: all.length, ready: all.filter((item) => item.state === "ready").length, references: all.filter((item) => item.referenceCount > 0).length, approvals: all.filter((item) => item.lockCount > 0).length, missing: all.filter((item) => item.state === "needs-reference").length };
  }, [cast, world]);

  async function selectRoom(nextRoom: KitRoom) {
    setRoom(nextRoom);
    const nextRecords = buildKitRecords(nextRoom, cast, world);
    const nextId = nextRecords[0]?.id ?? null;
    setSelectedId(nextId);
    setError("");
    if (nextRoom === "characters" && nextId) {
      try {
        const detail = await api.getMovieCharacterDetail(nextId);
        setCharacterDetail(detail.character);
      } catch (cause) {
        setError(cause instanceof Error ? cause.message : "The character could not be loaded.");
      }
    } else {
      setCharacterDetail(null);
    }
  }

  async function selectRecord(id: string) {
    setSelectedId(id);
    setError("");
    if (room !== "characters") return;
    try {
      const detail = await api.getMovieCharacterDetail(id);
      setCharacterDetail(detail.character);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "The character could not be loaded.");
    }
  }

  async function attachReference(assetId: string) {
    if (!selected || !assetId) return;
    setBusy("reference");
    setError("");
    try {
      if (room === "characters") {
        const source = characterDetail ?? (await api.getMovieCharacterDetail(selected.id)).character;
        const referenceAssetIds = Array.from(new Set([...source.referenceAssetIds, assetId]));
        await api.updateMovieCharacter(source.id, { name: source.name, role: source.role, description: source.description, appearance: source.appearance, physicalDescription: source.physicalDescription, wardrobe: source.wardrobe, voiceReference: source.voiceReference, personalityAndStoryNotes: source.personalityAndStoryNotes, voiceAndPerformance: source.voiceAndPerformance, continuityNotes: source.continuityNotes, referenceAssetId: referenceAssetIds[0] ?? null, referenceAssetIds });
      } else if (room === "locations" && selectedLocation) {
        await api.updateMovieLocation(selectedLocation.id, { name: selectedLocation.name, description: selectedLocation.description, visualContinuityNotes: selectedLocation.visualContinuityNotes, referenceAssetId: assetId });
      } else if (room === "props" && selectedProp) {
        await api.updateMovieProp(selectedProp.id, { name: selectedProp.name, description: selectedProp.description, category: selectedProp.category, continuityNotes: selectedProp.continuityNotes, referenceAssetId: assetId });
      }
      await load(room, selected.id);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "The reference could not be attached.");
    } finally {
      setBusy("");
    }
  }

  async function approveIdentity() {
    if (!selected || selected.lockCount > 0 || !selected.referenceCount) return;
    setBusy("approval");
    setError("");
    try {
      if (room === "characters") {
        const source = characterDetail ?? (await api.getMovieCharacterDetail(selected.id)).character;
        await api.lockMovieCharacterFact(source.id, { fieldKey: "name", lockedValue: source.name });
      } else {
        await api.addMovieContinuityLock(projectId, { entityType: room === "locations" ? "location" : "prop", entityId: selected.id, fieldName: "name", lockedValue: selected.name, strength: "hard", reason: "Production Kit identity approved." });
      }
      await load(room, selected.id);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "The identity approval could not be saved.");
    } finally {
      setBusy("");
    }
  }

  if (loading && !world) return <div className="movie-kit-loading"><span className="loading-spinner" /><span>Loading the Production Kit…</span></div>;
  if (!world || !cast) return <div className="movie-kit-empty"><ShieldCheck size={22} /><strong>Production Kit unavailable</strong><p>{error || "The project references could not be loaded."}</p></div>;

  return <div className="movie-module-stack movie-kit-room">
    <section className="movie-kit-hero"><div><span className="movie-workspace-kicker">{t("movieModule.production-kit.eyebrow")}</span><h3>{t("movieModule.production-kit.title")}</h3><p>{t("movieModule.production-kit.description")}</p></div><div className="movie-kit-hero-stat"><strong>{counts.ready}/{counts.total || 0}</strong><span>ready for production</span></div></section>
    <section className="movie-kit-readiness" aria-label="Production Kit readiness"><div className="movie-kit-readiness-heading"><div><span className="movie-workspace-kicker">Readiness</span><h4>Reference coverage at a glance</h4></div><span>{counts.total ? `${Math.round((counts.ready / counts.total) * 100)}% complete` : "No records yet"}</span></div><div className="movie-kit-progress"><span style={{ width: `${counts.total ? (counts.ready / counts.total) * 100 : 0}%` }} /></div><div className="movie-kit-readiness-stats"><span><strong>{counts.references}</strong> referenced</span><span><strong>{counts.approvals}</strong> approved</span><span className={counts.missing ? "is-alert" : ""}><strong>{counts.missing}</strong> missing reference</span></div></section>
    <div className="movie-kit-layout">
      <aside className="movie-kit-index"><div className="movie-kit-index-heading"><div><span className="movie-workspace-kicker">Production map</span><h4>Three anchor rooms</h4></div><Sparkles size={16} /></div><div className="movie-kit-room-tabs" role="tablist" aria-label="Production Kit rooms">{rooms.map((item) => { const Icon = item.icon; const items = buildKitRecords(item.id, cast, world); return <button key={item.id} type="button" role="tab" aria-selected={room === item.id} className={room === item.id ? "is-active" : ""} onClick={() => void selectRoom(item.id)}><Icon size={15} /><span><strong>{item.label}</strong><small>{item.hint}</small></span><em>{items.length}</em></button>; })}</div><div className="movie-kit-index-list">{records.length ? records.map((record, index) => { const Icon = recordIcon(record.room); return <button type="button" key={record.id} className={`movie-kit-record ${selected?.id === record.id ? "is-selected" : ""}`} onClick={() => void selectRecord(record.id)}><span className="movie-kit-record-number">{String(index + 1).padStart(2, "0")}</span><span className="movie-kit-record-icon"><Icon size={14} /></span><span className="movie-kit-record-copy"><strong>{record.name}</strong><small>{record.role || "Identity record"}</small></span><span className={`movie-kit-state-dot is-${record.state}`} aria-label={stateLabel(record.state)} /></button>; }) : <div className="movie-kit-list-empty"><Package size={18} /><strong>No {room} yet</strong><p>Open the full {room} room to add the first record.</p></div>}</div><Link className="movie-kit-open-room" href={`/create/movie/${projectId}/${room === "characters" ? "cast" : "world"}`}>Open full {room} room <ArrowUpRight size={13} /></Link></aside>
      <main className="movie-kit-inspector">{selected ? <><header className="movie-kit-inspector-header"><div><span className="movie-workspace-kicker">{selected.role || "Production identity"}</span><h4>{selected.name}</h4><p>{selected.description}</p></div><span className={`movie-kit-status-badge is-${selected.state}`}>{selected.state === "ready" ? <CheckCircle2 size={12} /> : <AlertTriangle size={12} />}{stateLabel(selected.state)}</span></header><div className="movie-kit-inspector-grid"><section className="movie-kit-reference-card"><div className="movie-kit-card-heading"><div><span className="movie-inspector-label">Reference anchor</span><h5>{selectedAsset?.name || "No reference attached"}</h5></div><ImageIcon size={16} /></div>{selectedAssetId ? <div className="movie-kit-reference-preview"><img src={assetFileUrl(selectedAssetId, true)} alt={`${selected.name} reference`} /></div> : <div className="movie-kit-reference-empty"><ImageIcon size={21} /><strong>Reference needed</strong><span>Production cannot carry this identity forward without a visual anchor.</span></div>}<div className="movie-kit-reference-actions"><label><span>{selectedAssetId ? "Change reference" : "Attach existing reference"}</span><select value="" onChange={(event) => void attachReference(event.target.value)} disabled={!imageAssets.length || busy === "reference"}><option value="">{imageAssets.length ? "Choose from Asset Library" : "No image assets available"}</option>{imageAssets.map((asset) => <option key={asset.id} value={asset.id}>{asset.name}</option>)}</select></label><Link href={`/assets?projectId=${encodeURIComponent(world.projectId ?? projectId)}&assetType=image`}><ArrowUpRight size={13} /> Open Asset Library</Link></div></section><section className="movie-kit-approval-card"><div className="movie-kit-card-heading"><div><span className="movie-inspector-label">Approval gate</span><h5>{selected.lockCount ? "Identity approved" : "Identity needs approval"}</h5></div><LockKeyhole size={16} /></div><div className={`movie-kit-lock-state ${selected.lockCount ? "is-locked" : ""}`}>{selected.lockCount ? <><CheckCircle2 size={16} /><div><strong>Protected continuity</strong><span>{selected.lockCount} approved lock{selected.lockCount === 1 ? "" : "s"} keep this identity stable.</span></div></> : <><AlertTriangle size={16} /><div><strong>Not locked yet</strong><span>Approve after the reference anchor is in place.</span></div></>}</div>{selectedLocks.length ? <div className="movie-kit-lock-list">{selectedLocks.slice(0, 4).map((lock) => <div key={lock.id}><LockKeyhole size={11} /><strong>{"fieldKey" in lock ? lock.fieldKey : lock.fieldName}</strong><span>{"lockedValue" in lock ? lock.lockedValue : "Approved continuity value"}</span></div>)}</div> : null}<button type="button" className="movie-workspace-button is-primary movie-kit-approve" onClick={() => void approveIdentity()} disabled={selected.state !== "needs-approval" || busy === "approval"}><ShieldCheck size={14} />{busy === "approval" ? "Saving approval…" : "Approve identity"}</button><p className="movie-kit-approval-note">Approval creates a durable lock. It does not generate media or change charging.</p></section></div><section className="movie-kit-handoff"><div><span className="movie-workspace-kicker">Handoff status</span><h5>{stateDetail(selected.state)}</h5><p>{selected.state === "ready" ? "This record can be carried into shot planning without re-deciding its identity." : selected.state === "needs-reference" ? "Attach a real image from the private Asset Library, then return here to approve the identity." : "The reference is present. Approve the identity when the creative team agrees it is canonical."}</p></div><div className="movie-kit-handoff-meta"><span><strong>{selected.referenceCount}</strong> reference{selected.referenceCount === 1 ? "" : "s"}</span><span><strong>{selected.lockCount}</strong> lock{selected.lockCount === 1 ? "" : "s"}</span></div></section></> : <div className="movie-kit-no-selection"><ShieldCheck size={24} /><strong>Choose a production record</strong><p>Readiness, references, and approval state will appear here.</p></div>}{error && <div className="movie-workspace-error-inline" role="alert"><span className="movie-error-icon">!</span><span>{error}</span></div>}</main>
    </div>
  </div>;
}

export function kitRecordForTest(room: KitRoom, cast: MovieCast | null, world: MovieWorldWorkspace | null) {
  return buildKitRecords(room, cast, world);
}
