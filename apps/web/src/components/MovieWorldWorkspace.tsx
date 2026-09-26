"use client";

import Link from "next/link";
import { useEffect, useMemo, useState } from "react";
import { ArrowUpRight, Check, Image as ImageIcon, LockKeyhole, MapPin, Package, Pencil, Plus, Save, ShieldCheck, Sparkles, Theater } from "lucide-react";
import { api, type MovieLocation, type MovieProp, type MovieSet, type MovieWorldAsset, type MovieWorldReference, type MovieWorldWorkspace as WorldData } from "@/lib/api";

// Kept local to the room so the rest of the Movie project does not need the complete graph.
type Room = "locations" | "sets" | "props";
type Entity = MovieLocation | MovieSet | MovieProp;
type FormState = { name: string; description: string; visualContinuityNotes: string; category: string; environmentType: string; movieLocationId: string; visualDescription: string; timeOfDay: string; weather: string; continuityNotes: string; referenceAssetId: string };

const rooms: { id: Room; label: string; icon: typeof MapPin; hint: string }[] = [
  { id: "locations", label: "Locations", icon: MapPin, hint: "The physical places" },
  { id: "sets", label: "Sets", icon: Theater, hint: "The built environments" },
  { id: "props", label: "Props", icon: Package, hint: "The continuity objects" },
];

const emptyForm: FormState = { name: "", description: "", visualContinuityNotes: "", category: "", environmentType: "practical", movieLocationId: "", visualDescription: "", timeOfDay: "", weather: "", continuityNotes: "", referenceAssetId: "" };

function formFor(room: Room, selected: Entity): FormState {
  if (room === "locations") { const location = selected as MovieLocation; return { ...emptyForm, name: location.name, description: location.description, visualContinuityNotes: location.visualContinuityNotes ?? "", referenceAssetId: location.referenceAssetId ?? "" }; }
  if (room === "sets") { const set = selected as MovieSet; return { ...emptyForm, name: set.name, description: set.description, environmentType: set.environmentType, movieLocationId: set.movieLocationId ?? "", visualDescription: set.visualDescription ?? "", timeOfDay: set.timeOfDay ?? "", weather: set.weather ?? "", continuityNotes: set.continuityNotes ?? "", referenceAssetId: set.referenceAssetId ?? "" }; }
  const prop = selected as MovieProp;
  return { ...emptyForm, name: prop.name, description: prop.description, category: prop.category ?? "", continuityNotes: prop.continuityNotes ?? "", referenceAssetId: prop.referenceAssetId ?? "" };
}

function assetLabel(assetId: string | null | undefined, assets: MovieWorldAsset[]) {
  return assets.find((asset) => asset.id === assetId);
}

export function MovieWorldWorkspace({ projectId }: { projectId: string }) {
  const [data, setData] = useState<WorldData | null>(null);
  const [room, setRoom] = useState<Room>("locations");
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [form, setForm] = useState<FormState>(emptyForm);
  const [editing, setEditing] = useState(false);
  const [creating, setCreating] = useState(false);
  const [saving, setSaving] = useState(false);
  const [addingReference, setAddingReference] = useState(false);
  const [referenceName, setReferenceName] = useState("");
  const [referenceKind, setReferenceKind] = useState("moodboard");
  const [referenceAssetId, setReferenceAssetId] = useState("");
  const [error, setError] = useState("");

  useEffect(() => {
    let mounted = true;
    void api.getMovieWorld(projectId).then((result) => {
      if (!mounted) return;
      setData(result);
      const first = result.world.locations[0];
      if (first) { setSelectedId(first.id); setForm(formFor("locations", first)); }
    }).catch((cause) => mounted && setError(cause instanceof Error ? cause.message : "The World room could not be loaded."));
    return () => { mounted = false; };
  }, [projectId]);

  const records = useMemo<Entity[]>(() => data ? room === "locations" ? data.world.locations : room === "sets" ? data.world.sets : data.world.props : [], [data, room]);
  const selected = records.find((item) => item.id === selectedId) ?? records[0] ?? null;
  const selectedSet = room === "sets" && selected && "variations" in selected ? selected : null;
  const selectedAssetId = selected && "referenceAssetId" in selected ? selected.referenceAssetId : null;
  const selectedAsset = assetLabel(selectedAssetId, data?.assets ?? []);

  function selectRoom(nextRoom: Room) {
    setRoom(nextRoom);
    const next = nextRoom === "locations" ? data?.world.locations[0] : nextRoom === "sets" ? data?.world.sets[0] : data?.world.props[0];
    setSelectedId(next?.id ?? null);
    setForm(next ? formFor(nextRoom, next) : emptyForm);
    setEditing(false);
  }

  function startCreate() { setCreating(true); setEditing(true); setSelectedId(null); setForm(emptyForm); setError(""); }
  function startEdit() { if (selected) { setCreating(false); setEditing(true); setError(""); } }
  function setField(key: keyof FormState, value: string) { setForm((current) => ({ ...current, [key]: value })); }

  async function save() {
    if (!data || !form.name.trim() || !form.description.trim()) return;
    setSaving(true); setError("");
    try {
      const referenceAssetId = form.referenceAssetId.trim() || null;
      if (creating) {
        const created = room === "locations"
          ? await api.addMovieLocation(projectId, { name: form.name, description: form.description, visualContinuityNotes: form.visualContinuityNotes || null, referenceAssetId })
          : room === "sets"
            ? await api.addMovieSet(projectId, { name: form.name, description: form.description, environmentType: form.environmentType, movieLocationId: form.movieLocationId || null, visualDescription: form.visualDescription || null, timeOfDay: form.timeOfDay || null, weather: form.weather || null, continuityNotes: form.continuityNotes || null, referenceAssetId })
            : await api.addMovieProp(projectId, { name: form.name, description: form.description, category: form.category || null, continuityNotes: form.continuityNotes || null, referenceAssetId });
        setData((current) => current ? { ...current, world: { ...current.world, [room]: [...(current.world[room] as Entity[]), created] } } : current);
        setSelectedId(created.id); setCreating(false); setEditing(false);
      } else if (selected) {
        const updated = room === "locations"
          ? await api.updateMovieLocation(selected.id, { name: form.name, description: form.description, visualContinuityNotes: form.visualContinuityNotes || null, referenceAssetId })
          : room === "sets"
            ? await api.updateMovieSet(selected.id, { name: form.name, description: form.description, environmentType: form.environmentType, movieLocationId: form.movieLocationId || null, visualDescription: form.visualDescription || null, timeOfDay: form.timeOfDay || null, weather: form.weather || null, continuityNotes: form.continuityNotes || null, referenceAssetId })
            : await api.updateMovieProp(selected.id, { name: form.name, description: form.description, category: form.category || null, continuityNotes: form.continuityNotes || null, referenceAssetId });
        setData((current) => current ? { ...current, world: { ...current.world, [room]: (current.world[room] as Entity[]).map((item) => item.id === updated.id ? updated : item) } } : current);
        setEditing(false);
      }
    } catch (cause) { setError(cause instanceof Error ? cause.message : "The World record could not be saved."); }
    finally { setSaving(false); }
  }

  async function saveReference() {
    if (!data || !referenceName.trim()) return;
    setSaving(true); setError("");
    try {
      const reference = await api.addMovieWorldReference(projectId, { name: referenceName.trim(), kind: referenceKind, assetId: referenceAssetId || null });
      setData((current) => current ? { ...current, world: { ...current.world, references: [...current.world.references, reference] } } : current);
      setReferenceName(""); setReferenceAssetId(""); setAddingReference(false);
    } catch (cause) { setError(cause instanceof Error ? cause.message : "The visual reference could not be registered."); }
    finally { setSaving(false); }
  }

  if (!data) return <div className="movie-world-loading"><span className="loading-spinner" /><p>{error || "Loading the World room…"}</p></div>;

  const usages = data.usageDetails.filter((usage) => usage.entityType === (room === "locations" ? "location" : room === "sets" ? "set" : "prop") && usage.entityId === selected?.id);
  const refs = data.world.references;

  return <div className="movie-world-room">
    <section className="movie-world-hero">
      <div><span className="movie-workspace-kicker">Visual production room</span><h3>Build the world once. Carry it into every shot.</h3><p>Identity, references, and continuity facts stay visible together — without loading the rest of the movie graph.</p></div>
      <div className="movie-world-signal"><Sparkles size={18} /><span>{data.world.locks.length} active locks</span><strong>{data.world.references.length} references</strong></div>
    </section>
    <nav className="movie-world-room-nav" aria-label="World rooms">{rooms.map((item) => { const Icon = item.icon; const count = item.id === "locations" ? data.world.locations.length : item.id === "sets" ? data.world.sets.length : data.world.props.length; return <button key={item.id} type="button" className={room === item.id ? "is-active" : ""} onClick={() => selectRoom(item.id)}><Icon size={15} /><span><strong>{item.label}</strong><small>{item.hint}</small></span><em>{count}</em></button>; })}</nav>
    <div className="movie-world-layout">
      <section className="movie-world-collection"><div className="movie-world-section-head"><div><span className="movie-workspace-kicker">{rooms.find((item) => item.id === room)?.label}</span><h4>{records.length ? `${records.length} records in this room` : "Start the room"}</h4></div><button type="button" className="movie-world-add" onClick={startCreate}><Plus size={14} /> New</button></div>
        {records.length ? <div className="movie-world-list">{records.map((item) => { const refAsset = assetLabel("referenceAssetId" in item ? item.referenceAssetId : null, data.assets); const locked = data.world.locks.some((lock) => lock.entityId === item.id); return <button type="button" key={item.id} className={`movie-world-list-item ${selected?.id === item.id ? "is-selected" : ""}`} onClick={() => { setSelectedId(item.id); setForm(formFor(room, item)); setEditing(false); }}><span className="movie-world-list-image">{refAsset?.canPreview ? <ImageIcon size={16} aria-hidden="true" /> : room === "locations" ? <MapPin size={16} /> : room === "sets" ? <Theater size={16} /> : <Package size={16} />}</span><span><strong>{item.name}</strong><small>{"category" in item ? item.category || "Continuity prop" : "environmentType" in item ? item.environmentType : "Physical location"}</small></span>{locked && <LockKeyhole size={13} className="is-locked" />}</button>; })}</div> : <div className="movie-world-empty"><MapPin size={19} /><strong>No {room} yet</strong><p>Add the first record to give production a visual anchor.</p><button type="button" className="movie-world-add" onClick={startCreate}><Plus size={14} /> Add {room.slice(0, -1)}</button></div>}
      </section>
      <section className="movie-world-inspector">
        {editing ? <WorldEditor room={room} form={form} locations={data.world.locations} assets={data.assets} onChange={setField} onCancel={() => { setEditing(false); setCreating(false); }} onSave={() => void save()} saving={saving} /> : selected ? <><div className="movie-world-inspector-head"><div><span className="movie-workspace-kicker">{room.slice(0, -1)} identity</span><h4>{selected.name}</h4></div><button type="button" className="movie-world-edit" onClick={startEdit}><Pencil size={13} /> Edit</button></div><p className="movie-world-description">{selected.description}</p><div className="movie-world-note-grid">{room === "locations" ? <Note label="Production / visual notes" value={(selected as MovieLocation).visualContinuityNotes} /> : room === "sets" ? <><Note label="Environment" value={(selected as MovieSet).environmentType} /><Note label="Visual direction" value={(selected as MovieSet).visualDescription} /><Note label="Time / weather" value={[(selected as MovieSet).timeOfDay, (selected as MovieSet).weather].filter(Boolean).join(" · ")} /><Note label="Continuity" value={(selected as MovieSet).continuityNotes} /></> : <><Note label="Ownership / usage context" value={(selected as MovieProp).category} /><Note label="Continuity" value={(selected as MovieProp).continuityNotes} /></>}</div>{selectedAsset && <AssetChip asset={selectedAsset} />}{selectedSet && <VariationRail set={selectedSet} onChange={(updated) => setData((current) => current ? { ...current, world: { ...current.world, sets: current.world.sets.map((item) => item.id === updated.id ? updated : item) } } : current)} />}</> : <div className="movie-world-empty"><Sparkles size={20} /><strong>Choose a world record</strong><p>Production identity, reference assets, and continuity locks will collect here.</p></div>}
        {selected && <UsageRail usages={usages} />}
      </section>
    </div>
    <div className="movie-world-bottom-grid"><section className="movie-world-support-card"><div className="movie-world-card-head"><div><span className="movie-workspace-kicker">Reference library</span><h4>Visual anchors</h4></div><div className="movie-world-card-actions"><Link href={`/assets?projectId=${encodeURIComponent(data.projectId ?? data.movieProjectId)}`}><ArrowUpRight size={14} /> Open Asset Library</Link><button type="button" className="movie-world-inline-add" onClick={() => setAddingReference((value) => !value)}><Plus size={12} /> Register reference</button></div></div>{addingReference && <div className="movie-world-reference-form"><input value={referenceName} onChange={(event) => setReferenceName(event.target.value)} placeholder="Harbor palette / wardrobe board" /><select value={referenceKind} onChange={(event) => setReferenceKind(event.target.value)}><option value="moodboard">Moodboard</option><option value="location">Location</option><option value="set">Set</option><option value="prop">Prop</option><option value="continuity">Continuity</option></select><select value={referenceAssetId} onChange={(event) => setReferenceAssetId(event.target.value)}><option value="">No Asset link</option>{data.assets.map((asset) => <option key={asset.id} value={asset.id}>{asset.name}</option>)}</select><button type="button" className="movie-world-save" onClick={() => void saveReference()} disabled={saving || !referenceName.trim()}><Save size={13} /> Save reference</button></div>}{refs.length ? <div className="movie-world-reference-grid">{refs.map((reference) => <ReferenceCard key={reference.id} reference={reference} assets={data.assets} />)}</div> : <p className="movie-world-muted">No linked moodboards or visual references yet. Register one here or use the Asset Library to keep production references reusable.</p>}</section><section className="movie-world-support-card"><div className="movie-world-card-head"><div><span className="movie-workspace-kicker">Continuity desk</span><h4>Facts and locks</h4></div><ShieldCheck size={16} /></div><div className="movie-world-facts">{data.world.facts.map((fact) => <div key={fact.id}><span>{fact.scopeType} · {fact.factKey}</span><strong>{fact.factValue}</strong>{fact.notes && <small>{fact.notes}</small>}</div>)}{data.world.locks.map((lock) => <div className="movie-world-lock" key={lock.id}><span><LockKeyhole size={11} /> {lock.strength} lock · {lock.entityType}</span><strong>{lock.fieldName}</strong><small>{lock.lockedValue}{lock.reason ? ` · ${lock.reason}` : ""}</small></div>)}{!data.world.facts.length && !data.world.locks.length && <p className="movie-world-muted">No continuity facts or locks have been recorded yet.</p>}</div></section></div>
    {error && <div className="movie-workspace-error-inline"><XCircleIcon /> {error}</div>}
  </div>;
}

function WorldEditor({ room, form, locations, assets, onChange, onCancel, onSave, saving }: { room: Room; form: FormState; locations: MovieLocation[]; assets: MovieWorldAsset[]; onChange: (key: keyof FormState, value: string) => void; onCancel: () => void; onSave: () => void; saving: boolean }) {
  const type = room === "locations" ? "location" : room === "sets" ? "set" : "prop";
  return <div className="movie-world-editor"><div className="movie-world-inspector-head"><div><span className="movie-workspace-kicker">{type} editor</span><h4>Production identity</h4></div><span className="movie-world-editor-badge"><ShieldCheck size={12} /> Locked facts stay protected</span></div><label><span>Name</span><input value={form.name} onChange={(event) => onChange("name", event.target.value)} placeholder={`${type} name`} /></label><label><span>Description</span><textarea value={form.description} onChange={(event) => onChange("description", event.target.value)} placeholder="What production needs to know" /></label>{room === "locations" && <label><span>Production / visual notes</span><textarea value={form.visualContinuityNotes} onChange={(event) => onChange("visualContinuityNotes", event.target.value)} placeholder="Light, texture, geography, visual rules" /></label>}{room === "sets" && <><div className="movie-world-field-grid"><label><span>Environment</span><input value={form.environmentType} onChange={(event) => onChange("environmentType", event.target.value)} /></label><label><span>Location relationship</span><select value={form.movieLocationId} onChange={(event) => onChange("movieLocationId", event.target.value)}><option value="">Independent set</option>{locations.map((location) => <option key={location.id} value={location.id}>{location.name}</option>)}</select></label><label><span>Time of day</span><input value={form.timeOfDay} onChange={(event) => onChange("timeOfDay", event.target.value)} /></label><label><span>Weather</span><input value={form.weather} onChange={(event) => onChange("weather", event.target.value)} /></label></div><label><span>Visual direction</span><textarea value={form.visualDescription} onChange={(event) => onChange("visualDescription", event.target.value)} /></label><label><span>Continuity / production notes</span><textarea value={form.continuityNotes} onChange={(event) => onChange("continuityNotes", event.target.value)} /></label></>}{room === "props" && <><label><span>Ownership / usage context</span><input value={form.category} onChange={(event) => onChange("category", event.target.value)} placeholder="Hero prop, set dressing, practical…" /></label><label><span>Continuity information</span><textarea value={form.continuityNotes} onChange={(event) => onChange("continuityNotes", event.target.value)} /></label></>}<label><span>Reference Asset <small>Reuse a linked record from the Asset Library</small></span><select value={form.referenceAssetId} onChange={(event) => onChange("referenceAssetId", event.target.value)}><option value="">No reference linked</option>{assets.map((asset) => <option key={asset.id} value={asset.id}>{asset.name} · {asset.assetType}</option>)}</select></label><div className="movie-world-editor-actions"><button type="button" className="movie-world-cancel" onClick={onCancel}>Cancel</button><button type="button" className="movie-world-save" onClick={onSave} disabled={saving || !form.name.trim() || !form.description.trim()}><Save size={14} />{saving ? "Saving…" : "Save identity"}</button></div></div>;
}

function VariationRail({ set, onChange }: { set: MovieSet; onChange: (set: MovieSet) => void }) { const [adding, setAdding] = useState(false); const [name, setName] = useState(""); async function add() { if (!name.trim()) return; const variation = await api.addMovieSetVariation(set.id, { name: name.trim(), isDefault: set.variations.length === 0 }); onChange({ ...set, variations: [...set.variations, variation] }); setName(""); setAdding(false); } return <div className="movie-world-variations"><div className="movie-world-card-head"><div><span className="movie-workspace-kicker">Set states</span><h5>Variations</h5></div><button type="button" className="movie-world-inline-add" onClick={() => setAdding((value) => !value)}><Plus size={12} /> Add state</button></div>{adding && <div className="movie-world-inline-form"><input value={name} onChange={(event) => setName(event.target.value)} placeholder="Night / rain / aftermath" /><button type="button" onClick={() => void add()}><Check size={13} /></button></div>}{set.variations.length ? <div className="movie-world-variation-list">{set.variations.map((variation) => <div key={variation.id} className={variation.isDefault ? "is-default" : ""}><span>{variation.name}</span>{variation.isDefault && <em>Default</em>}<small>{[variation.timeOfDay, variation.weather, variation.lighting].filter(Boolean).join(" · ") || "State details not set"}</small></div>)}</div> : <p className="movie-world-muted">No states yet. Add a default variation when the set has a production look.</p>}</div>; }
function UsageRail({ usages }: { usages: WorldData["usageDetails"] }) { return <div className="movie-world-usage"><div className="movie-world-card-head"><div><span className="movie-workspace-kicker">Scene / shot usage</span><h5>Where this record travels</h5></div><span>{usages.length} uses</span></div>{usages.length ? usages.map((usage) => <div className="movie-world-usage-row" key={usage.id}><span>{String(usage.sceneSequence).padStart(2, "0")}</span><div><strong>{usage.sceneTitle}</strong><small>{usage.shotSequence ? `Shot ${usage.shotSequence} · ${usage.shotDescription ?? ""}` : "Scene-wide"}{usage.role ? ` · ${usage.role}` : ""}</small></div></div>) : <p className="movie-world-muted">Not attached to a scene or shot yet.</p>}</div>; }
function Note({ label, value }: { label: string; value?: string | null }) { return <div><span>{label}</span><p>{value || "Not set yet"}</p></div>; }
function AssetChip({ asset }: { asset: MovieWorldAsset }) { return <div className="movie-world-asset-chip"><ImageIcon size={14} aria-hidden="true" /><span><small>Reference asset</small><strong>{asset.name}</strong></span><Link href={`/assets?search=${encodeURIComponent(asset.name)}`}><ArrowUpRight size={13} /></Link></div>; }
function ReferenceCard({ reference, assets }: { reference: MovieWorldReference; assets: MovieWorldAsset[] }) { const asset = assetLabel(reference.assetId, assets); return <Link href={asset ? `/assets?search=${encodeURIComponent(asset.name)}` : "#"} className="movie-world-reference"><span className="movie-world-reference-mark"><ImageIcon size={16} aria-hidden="true" /></span><span><strong>{reference.name}</strong><small>{reference.kind}{asset ? ` · ${asset.name}` : " · No Asset linked"}</small></span><ArrowUpRight size={13} /></Link>; }
function XCircleIcon() { return <span className="movie-error-icon" aria-hidden="true">!</span>; }
