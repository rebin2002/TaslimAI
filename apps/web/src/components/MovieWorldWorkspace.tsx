"use client";

import Link from "next/link";
import { useEffect, useMemo, useState } from "react";
import { ArrowUpRight, Check, Compass, DoorOpen, Image as ImageIcon, LockKeyhole, MapPin, Package, Pencil, Plus, Route, Save, ShieldCheck, Sparkles, Sun, Theater } from "lucide-react";
import { useLocale } from "@/components/LocaleProvider";
import { api, type MovieLocation, type MovieLocationGeographyEntry, type MovieLocationGeographyOpening, type MovieLocationGeographyPath, type MovieLocationGeographySheet, type MovieProp, type MovieSet, type MovieWorldAsset, type MovieWorldReference, type MovieWorldWorkspace as WorldData } from "@/lib/api";

// Kept local to the room so the rest of the Movie project does not need the complete graph.
type Room = "locations" | "sets" | "props";
type Entity = MovieLocation | MovieSet | MovieProp;
type FormState = { name: string; description: string; visualContinuityNotes: string; category: string; environmentType: string; movieLocationId: string; visualDescription: string; timeOfDay: string; weather: string; continuityNotes: string; referenceAssetId: string };

const rooms: { id: Room; label: string; icon: typeof MapPin; hint: string }[] = [
  { id: "locations", label: "locations", icon: MapPin, hint: "locationsHint" },
  { id: "sets", label: "sets", icon: Theater, hint: "setsHint" },
  { id: "props", label: "props", icon: Package, hint: "propsHint" },
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
  const { t } = useLocale();
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

  if (!data) return <div className="movie-world-loading"><span className="loading-spinner" /><p>{error || t("movieBody.loading")}</p></div>;

  const usages = data.usageDetails.filter((usage) => usage.entityType === (room === "locations" ? "location" : room === "sets" ? "set" : "prop") && usage.entityId === selected?.id);
  const refs = data.world.references;

  return <div className="movie-world-room">
    <header className="movie-world-project-heading"><span className="movie-workspace-kicker">{t("movieModule.world.label")} · {data.status}</span><h1>{data.title}</h1><p>{data.description}</p></header>
    <section className="movie-world-hero">
      <div><span className="movie-workspace-kicker">{t("movieBody.world.visualRoom")}</span><h3>{t("movieBody.world.heroTitle")}</h3><p>{t("movieBody.world.heroText")}</p></div>
      <div className="movie-world-signal"><Sparkles size={18} /><span>{data.world.locks.length} {t("movieBody.world.activeLocks")}</span><strong>{data.world.references.length} {t("movieBody.world.references")}</strong></div>
    </section>
    <nav className="movie-world-room-nav" aria-label="World rooms">{rooms.map((item) => { const Icon = item.icon; const count = item.id === "locations" ? data.world.locations.length : item.id === "sets" ? data.world.sets.length : data.world.props.length; return <button key={item.id} type="button" className={room === item.id ? "is-active" : ""} onClick={() => selectRoom(item.id)}><Icon size={15} /><span><strong>{t(`movieBody.world.${item.label}`)}</strong><small>{t(`movieBody.world.${item.hint}`)}</small></span><em>{count}</em></button>; })}</nav>
    <div className="movie-world-layout">
      <section className="movie-world-collection"><div className="movie-world-section-head"><div><span className="movie-workspace-kicker">{rooms.find((item) => item.id === room)?.label}</span><h4>{records.length ? `${records.length} ${t("movieBody.world.records")}` : t("movieBody.world.start")}</h4></div><button type="button" className="movie-world-add" onClick={startCreate}><Plus size={14} /> {t("movieBody.new")}</button></div>
        {records.length ? <div className="movie-world-list">{records.map((item) => { const refAsset = assetLabel("referenceAssetId" in item ? item.referenceAssetId : null, data.assets); const locked = data.world.locks.some((lock) => lock.entityId === item.id); return <button type="button" key={item.id} className={`movie-world-list-item ${selected?.id === item.id ? "is-selected" : ""}`} onClick={() => { setSelectedId(item.id); setForm(formFor(room, item)); setEditing(false); }}><span className="movie-world-list-image">{refAsset?.canPreview ? <ImageIcon size={16} aria-hidden="true" /> : room === "locations" ? <MapPin size={16} /> : room === "sets" ? <Theater size={16} /> : <Package size={16} />}</span><span><strong>{item.name}</strong><small>{"category" in item ? item.category || "Continuity prop" : "environmentType" in item ? item.environmentType : "Physical location"}</small></span>{locked && <LockKeyhole size={13} className="is-locked" />}</button>; })}</div> : <div className="movie-world-empty"><MapPin size={19} /><strong>{t("movieBody.noRecords")}</strong><p>{t("movieBody.world.addFirst")}</p><button type="button" className="movie-world-add" onClick={startCreate}><Plus size={14} /> {t("movieBody.new")}</button></div>}
      </section>
      <section className="movie-world-inspector">
        {editing ? <WorldEditor room={room} form={form} locations={data.world.locations} assets={data.assets} onChange={setField} onCancel={() => { setEditing(false); setCreating(false); }} onSave={() => void save()} saving={saving} /> : selected ? <><div className="movie-world-inspector-head"><div><span className="movie-workspace-kicker">{room.slice(0, -1)} identity</span><h3>{selected.name}</h3></div><button type="button" className="movie-world-edit" onClick={startEdit}><Pencil size={13} /> {t("movieBody.edit")}</button></div><p className="movie-world-description">{selected.description}</p><div className="movie-world-note-grid">{room === "locations" ? <Note label="Production / visual notes" value={(selected as MovieLocation).visualContinuityNotes} /> : room === "sets" ? <><Note label="Environment" value={(selected as MovieSet).environmentType} /><Note label="Visual direction" value={(selected as MovieSet).visualDescription} /><Note label="Time / weather" value={[(selected as MovieSet).timeOfDay, (selected as MovieSet).weather].filter(Boolean).join(" · ")} /><Note label="Continuity" value={(selected as MovieSet).continuityNotes} /></> : <><Note label="Ownership / usage context" value={(selected as MovieProp).category} /><Note label="Continuity" value={(selected as MovieProp).continuityNotes} /></>}</div>{selectedAsset && <AssetChip asset={selectedAsset} />}{room === "locations" && <GeographySheetPanel key={`${selected.id}:${(selected as MovieLocation).geographySheet?.updatedAt ?? "empty"}`} location={selected as MovieLocation} assets={data.assets} onSaved={(sheet) => setData((current) => current ? { ...current, world: { ...current.world, locations: current.world.locations.map((item) => item.id === selected.id ? { ...item, geographySheet: sheet } : item) } } : current)} />}{selectedSet && <VariationRail set={selectedSet} onChange={(updated) => setData((current) => current ? { ...current, world: { ...current.world, sets: current.world.sets.map((item) => item.id === updated.id ? updated : item) } } : current)} />}</> : <div className="movie-world-empty"><Sparkles size={20} /><strong>{t("movieBody.world.chooseRecord")}</strong><p>{t("movieBody.productionIdentity")}</p></div>}
        {selected && <UsageRail usages={usages} />}
      </section>
    </div>
    <div className="movie-world-bottom-grid"><section className="movie-world-support-card"><div className="movie-world-card-head"><div><span className="movie-workspace-kicker">{t("movieBody.world.referenceLibrary")}</span><h4>{t("movieBody.world.visualAnchors")}</h4></div><div className="movie-world-card-actions"><Link href={`/assets?projectId=${encodeURIComponent(data.projectId ?? data.movieProjectId)}`}><ArrowUpRight size={14} /> {t("movieBody.world.openLibrary")}</Link><button type="button" className="movie-world-inline-add" onClick={() => setAddingReference((value) => !value)}><Plus size={12} /> {t("movieBody.world.registered")}</button></div></div>{addingReference && <div className="movie-world-reference-form"><input value={referenceName} onChange={(event) => setReferenceName(event.target.value)} placeholder="Harbor palette / wardrobe board" /><select value={referenceKind} onChange={(event) => setReferenceKind(event.target.value)}><option value="moodboard">Moodboard</option><option value="location">Location</option><option value="set">Set</option><option value="prop">Prop</option><option value="continuity">Continuity</option></select><select value={referenceAssetId} onChange={(event) => setReferenceAssetId(event.target.value)}><option value="">No Asset link</option>{data.assets.map((asset) => <option key={asset.id} value={asset.id}>{asset.name}</option>)}</select><button type="button" className="movie-world-save" onClick={() => void saveReference()} disabled={saving || !referenceName.trim()}><Save size={13} /> Save reference</button></div>}{refs.length ? <div className="movie-world-reference-grid">{refs.map((reference) => <ReferenceCard key={reference.id} reference={reference} assets={data.assets} />)}</div> : <p className="movie-world-muted">No linked moodboards or visual references yet. Register one here or use the Asset Library to keep production references reusable.</p>}</section><section className="movie-world-support-card"><div className="movie-world-card-head"><div><span className="movie-workspace-kicker">{t("movieBody.world.continuityDeskText")}</span><h4>{t("movieBody.world.factsLocksText")}</h4></div><ShieldCheck size={16} /></div><div className="movie-world-facts">{data.world.facts.map((fact) => <div key={fact.id}><span>{fact.scopeType} · {fact.factKey}</span><strong>{fact.factValue}</strong>{fact.notes && <small>{fact.notes}</small>}</div>)}{data.world.locks.map((lock) => <div className="movie-world-lock" key={lock.id}><span><LockKeyhole size={11} /> {lock.strength} lock · {lock.entityType}</span><strong>{lock.fieldName}</strong><small>{lock.lockedValue}{lock.reason ? ` · ${lock.reason}` : ""}</small></div>)}{!data.world.facts.length && !data.world.locks.length && <p className="movie-world-muted">No continuity facts or locks have been recorded yet.</p>}</div></section></div>
    {error && <div className="movie-workspace-error-inline"><XCircleIcon /> {error}</div>}
  </div>;
}

function WorldEditor({
  room, form, locations, assets, onChange, onCancel, onSave, saving }: { room: Room; form: FormState; locations: MovieLocation[]; assets: MovieWorldAsset[]; onChange: (key: keyof FormState, value: string) => void; onCancel: () => void; onSave: () => void; saving: boolean }) {
  const { t } = useLocale();
  const type = room === "locations" ? "location" : room === "sets" ? "set" : "prop";
  return <div className="movie-world-editor"><div className="movie-world-inspector-head"><div><span className="movie-workspace-kicker">{type} editor</span><h4>Production identity</h4></div><span className="movie-world-editor-badge"><ShieldCheck size={12} /> Locked facts stay protected</span></div><label><span>Name</span><input value={form.name} onChange={(event) => onChange("name", event.target.value)} placeholder={`${type} name`} /></label><label><span>Description</span><textarea value={form.description} onChange={(event) => onChange("description", event.target.value)} placeholder="What production needs to know" /></label>{room === "locations" && <label><span>Production / visual notes</span><textarea value={form.visualContinuityNotes} onChange={(event) => onChange("visualContinuityNotes", event.target.value)} placeholder="Light, texture, geography, visual rules" /></label>}{room === "sets" && <><div className="movie-world-field-grid"><label><span>Environment</span><input value={form.environmentType} onChange={(event) => onChange("environmentType", event.target.value)} /></label><label><span>Location relationship</span><select value={form.movieLocationId} onChange={(event) => onChange("movieLocationId", event.target.value)}><option value="">Independent set</option>{locations.map((location) => <option key={location.id} value={location.id}>{location.name}</option>)}</select></label><label><span>Time of day</span><input value={form.timeOfDay} onChange={(event) => onChange("timeOfDay", event.target.value)} /></label><label><span>Weather</span><input value={form.weather} onChange={(event) => onChange("weather", event.target.value)} /></label></div><label><span>Visual direction</span><textarea value={form.visualDescription} onChange={(event) => onChange("visualDescription", event.target.value)} /></label><label><span>Continuity / production notes</span><textarea value={form.continuityNotes} onChange={(event) => onChange("continuityNotes", event.target.value)} /></label></>}{room === "props" && <><label><span>Ownership / usage context</span><input value={form.category} onChange={(event) => onChange("category", event.target.value)} placeholder="Hero prop, set dressing, practical…" /></label><label><span>Continuity information</span><textarea value={form.continuityNotes} onChange={(event) => onChange("continuityNotes", event.target.value)} /></label></>}<label><span>{t("movieBody.world.referenceAsset")} <small>Reuse a linked record from the Asset Library</small></span><select value={form.referenceAssetId} onChange={(event) => onChange("referenceAssetId", event.target.value)}><option value="">{t("movieBody.world.noReference")}</option>{assets.map((asset) => <option key={asset.id} value={asset.id}>{asset.name} · {asset.assetType}</option>)}</select></label><div className="movie-world-editor-actions"><button type="button" className="movie-world-cancel" onClick={onCancel}>{t("movieBody.cancel")}</button><button type="button" className="movie-world-save" onClick={onSave} disabled={saving || !form.name.trim() || !form.description.trim()}><Save size={14} />{saving ? "Saving…" : t("movieBody.world.saveIdentity")}</button></div></div>;
}

function VariationRail({
  set, onChange }: { set: MovieSet; onChange: (set: MovieSet) => void }) { const [adding, setAdding] = useState(false); const [name, setName] = useState(""); async function add() { if (!name.trim()) return; const variation = await api.addMovieSetVariation(set.id, { name: name.trim(), isDefault: set.variations.length === 0 }); onChange({ ...set, variations: [...set.variations, variation] }); setName(""); setAdding(false); } return <div className="movie-world-variations"><div className="movie-world-card-head"><div><span className="movie-workspace-kicker">Set states</span><h5>Variations</h5></div><button type="button" className="movie-world-inline-add" onClick={() => setAdding((value) => !value)}><Plus size={12} /> Add state</button></div>{adding && <div className="movie-world-inline-form"><input value={name} onChange={(event) => setName(event.target.value)} placeholder="Night / rain / aftermath" /><button type="button" onClick={() => void add()}><Check size={13} /></button></div>}{set.variations.length ? <div className="movie-world-variation-list">{set.variations.map((variation) => <div key={variation.id} className={variation.isDefault ? "is-default" : ""}><span>{variation.name}</span>{variation.isDefault && <em>Default</em>}<small>{[variation.timeOfDay, variation.weather, variation.lighting].filter(Boolean).join(" · ") || "State details not set"}</small></div>)}</div> : <p className="movie-world-muted">No states yet. Add a default variation when the set has a production look.</p>}</div>; }
function UsageRail({
  usages,
}: { usages: WorldData["usageDetails"] }) { return <div className="movie-world-usage"><div className="movie-world-card-head"><div><span className="movie-workspace-kicker">Scene / shot usage</span><h5>Where this record travels</h5></div><span>{usages.length} uses</span></div>{usages.length ? usages.map((usage) => <div className="movie-world-usage-row" key={usage.id}><span>{String(usage.sceneSequence).padStart(2, "0")}</span><div><strong>{usage.sceneTitle}</strong><small>{usage.shotSequence ? `Shot ${usage.shotSequence} · ${usage.shotDescription ?? ""}` : "Scene-wide"}{usage.role ? ` · ${usage.role}` : ""}</small></div></div>) : <p className="movie-world-muted">Not attached to a scene or shot yet.</p>}</div>; }

type GeographyDraft = { establishingReferenceAssetId: string; establishingReferenceNotes: string; wideThreeQuarterReferenceAssetId: string; wideThreeQuarterReferenceNotes: string; entrancesExits: string; windows: string; paths: string; majorObjects: string; lightSources: string; orientationAnchors: string };
const emptyGeographyDraft: GeographyDraft = { establishingReferenceAssetId: "", establishingReferenceNotes: "", wideThreeQuarterReferenceAssetId: "", wideThreeQuarterReferenceNotes: "", entrancesExits: "", windows: "", paths: "", majorObjects: "", lightSources: "", orientationAnchors: "" };
const joinEntry = (item: { label: string; description: string }) => `${item.label} | ${item.description}`;
const parseEntryLines = (value: string): MovieLocationGeographyEntry[] => value.split("\n").map((line) => line.trim()).filter(Boolean).map((line) => { const [label, ...description] = line.split("|"); return { label: label.trim(), description: description.join("|").trim() || label.trim() }; });
const parseOpeningLines = (value: string): MovieLocationGeographyOpening[] => parseEntryLines(value).map((item) => ({ label: item.label, kind: "opening", description: item.description }));
const parsePathLines = (value: string): MovieLocationGeographyPath[] => value.split("\n").map((line) => line.trim()).filter(Boolean).map((line) => { const [label, from, to, ...description] = line.split("|"); return { label: label?.trim() || "Path", from: from?.trim() || "Origin", to: to?.trim() || "Destination", description: description.join("|").trim() || "Spatial path" }; });
function geographyDraft(sheet: MovieLocationGeographySheet | null | undefined): GeographyDraft { return !sheet ? emptyGeographyDraft : { establishingReferenceAssetId: sheet.establishingReferenceAssetId ?? "", establishingReferenceNotes: sheet.establishingReferenceNotes ?? "", wideThreeQuarterReferenceAssetId: sheet.wideThreeQuarterReferenceAssetId ?? "", wideThreeQuarterReferenceNotes: sheet.wideThreeQuarterReferenceNotes ?? "", entrancesExits: sheet.entrancesExits.map(joinEntry).join("\n"), windows: sheet.windows.map(joinEntry).join("\n"), paths: sheet.paths.map((item) => `${item.label} | ${item.from} | ${item.to} | ${item.description}`).join("\n"), majorObjects: sheet.majorObjects.map(joinEntry).join("\n"), lightSources: sheet.lightSources.map(joinEntry).join("\n"), orientationAnchors: sheet.orientationAnchors.map(joinEntry).join("\n") }; }

function GeographySheetPanel({
  location, assets, onSaved }: { location: MovieLocation; assets: MovieWorldAsset[]; onSaved: (sheet: MovieLocationGeographySheet) => void }) {
  const [draft, setDraft] = useState<GeographyDraft>(() => geographyDraft(location.geographySheet));
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState("");
  const [variantName, setVariantName] = useState("");
  const [variantSaving, setVariantSaving] = useState(false);
  const sheet = location.geographySheet;
  function change(key: keyof GeographyDraft, value: string) { setDraft((current) => ({ ...current, [key]: value })); }
  async function save() {
    setSaving(true); setError("");
    try { const next = await api.upsertMovieLocationGeographySheet(location.id, { establishingReferenceAssetId: draft.establishingReferenceAssetId || null, establishingReferenceNotes: draft.establishingReferenceNotes || null, wideThreeQuarterReferenceAssetId: draft.wideThreeQuarterReferenceAssetId || null, wideThreeQuarterReferenceNotes: draft.wideThreeQuarterReferenceNotes || null, entrancesExits: parseOpeningLines(draft.entrancesExits), windows: parseOpeningLines(draft.windows), paths: parsePathLines(draft.paths), majorObjects: parseEntryLines(draft.majorObjects), lightSources: parseEntryLines(draft.lightSources), orientationAnchors: parseEntryLines(draft.orientationAnchors) }); onSaved(next); }
    catch (cause) { setError(cause instanceof Error ? cause.message : "The geography sheet could not be saved."); }
    finally { setSaving(false); }
  }
  async function approve() { setSaving(true); setError(""); try { onSaved(await api.approveMovieLocationGeographySheet(location.id)); } catch (cause) { setError(cause instanceof Error ? cause.message : "The geography sheet could not be approved."); } finally { setSaving(false); } }
  async function addVariant() { if (!variantName.trim()) return; setVariantSaving(true); setError(""); try { const variant = await api.addMovieLocationGeographyVariant(location.id, { name: variantName.trim() }); const next = { ...(location.geographySheet ?? { ...emptyGeographyDraft }), variants: [...(location.geographySheet?.variants ?? []), variant] } as MovieLocationGeographySheet; onSaved(next); setVariantName(""); } catch (cause) { setError(cause instanceof Error ? cause.message : "The location variant could not be saved."); } finally { setVariantSaving(false); } }
  return <section className="movie-world-geography"><div className="movie-world-card-head"><div><span className="movie-workspace-kicker">Location geography sheet</span><h5>Lock the space before expensive motion</h5></div><span className={sheet?.status === "Approved" ? "movie-world-status is-approved" : "movie-world-status"}>{sheet?.status ?? "Draft"}</span></div><p className="movie-world-muted">A reusable spatial contract for establishing coverage, 3/4 wides, entrances, paths, objects, light, and orientation. It travels with the World and Visual Bible into continuity snapshots.</p><div className="movie-world-geography-reference-grid"><label><span><Compass size={11} /> Establishing reference</span><select value={draft.establishingReferenceAssetId} onChange={(event) => change("establishingReferenceAssetId", event.target.value)} disabled={sheet?.status === "Approved"}><option value="">No Asset selected</option>{assets.map((asset) => <option key={asset.id} value={asset.id}>{asset.name}</option>)}</select><textarea value={draft.establishingReferenceNotes} onChange={(event) => change("establishingReferenceNotes", event.target.value)} placeholder="What the establishing frame must prove" disabled={sheet?.status === "Approved"} /></label><label><span><Compass size={11} /> Wide 3/4 spatial reference</span><select value={draft.wideThreeQuarterReferenceAssetId} onChange={(event) => change("wideThreeQuarterReferenceAssetId", event.target.value)} disabled={sheet?.status === "Approved"}><option value="">No Asset selected</option>{assets.map((asset) => <option key={asset.id} value={asset.id}>{asset.name}</option>)}</select><textarea value={draft.wideThreeQuarterReferenceNotes} onChange={(event) => change("wideThreeQuarterReferenceNotes", event.target.value)} placeholder="Camera-facing spatial relationship" disabled={sheet?.status === "Approved"} /></label></div><div className="movie-world-geography-grid"><GeographyField icon={<DoorOpen size={12} />} label="Entrances / exits" value={draft.entrancesExits} onChange={(value) => change("entrancesExits", value)} placeholder="Front door | Entry from courtyard" disabled={sheet?.status === "Approved"} /><GeographyField icon={<ImageIcon size={12} />} label="Windows" value={draft.windows} onChange={(value) => change("windows", value)} placeholder="North window | Deep exterior view" disabled={sheet?.status === "Approved"} /><GeographyField icon={<Route size={12} />} label="Paths" value={draft.paths} onChange={(value) => change("paths", value)} placeholder="Main path | Door | Tree | Actor route" disabled={sheet?.status === "Approved"} /><GeographyField icon={<Package size={12} />} label="Major objects" value={draft.majorObjects} onChange={(value) => change("majorObjects", value)} placeholder="Table | Left of hearth" disabled={sheet?.status === "Approved"} /><GeographyField icon={<Sun size={12} />} label="Light sources" value={draft.lightSources} onChange={(value) => change("lightSources", value)} placeholder="Window light | North-facing soft source" disabled={sheet?.status === "Approved"} /><GeographyField icon={<Compass size={12} />} label="Orientation anchors" value={draft.orientationAnchors} onChange={(value) => change("orientationAnchors", value)} placeholder="North wall | Camera left" disabled={sheet?.status === "Approved"} /></div><div className="movie-world-geography-actions"><small>{sheet ? `Guide revision ${sheet.guideRevisionNumber} · continuity ${sheet.continuitySnapshotHash ? "hashed" : "pending"}` : "Not yet captured in continuity"}</small>{sheet?.status !== "Approved" && <><button type="button" className="movie-world-save" onClick={() => void save()} disabled={saving}>{saving ? "Saving…" : <><Save size={13} /> Save sheet</>}</button>{sheet && <button type="button" className="movie-world-inline-add" onClick={() => void approve()} disabled={saving}><ShieldCheck size={13} /> Approve sheet</button>}</>}</div>{sheet?.worldBibleJson && <details className="movie-world-bible"><summary>World / Visual Bible context captured</summary><small>World Bible and Visual Bible references are pinned to this sheet revision for continuity review.</small></details>}<div className="movie-world-variants"><div className="movie-world-card-head"><div><span className="movie-workspace-kicker">Approved variants</span><h5>Controlled time, weather, and light</h5></div><span>{sheet?.variants.filter((item) => item.status === "Approved").length ?? 0} approved</span></div><div className="movie-world-inline-form"><input value={variantName} onChange={(event) => setVariantName(event.target.value)} placeholder="Night / rain / aftermath" disabled={!sheet} /><button type="button" onClick={() => void addVariant()} disabled={variantSaving || !sheet || !variantName.trim()}><Plus size={13} /></button></div>{sheet?.variants.length ? <div className="movie-world-variant-list">{sheet.variants.map((variant) => <div key={variant.id}><span>{variant.name}</span><em>{variant.status}</em>{variant.status !== "Approved" && <button type="button" onClick={() => void api.approveMovieLocationGeographyVariant(variant.id).then((approved) => onSaved({ ...sheet, variants: sheet.variants.map((item) => item.id === approved.id ? approved : item) })).catch((cause) => setError(cause instanceof Error ? cause.message : "The variant could not be approved."))}>Approve</button>}</div>)}</div> : <p className="movie-world-muted">Save a sheet to define controlled variants.</p>}</div>{error && <div className="movie-workspace-error-inline" role="alert">{error}</div>}</section>;
}

function GeographyField({
  icon, label, value, onChange, placeholder, disabled }: { icon: React.ReactNode; label: string; value: string; onChange: (value: string) => void; placeholder: string; disabled?: boolean }) { return <label className="movie-world-geography-field"><span>{icon} {label}</span><textarea value={value} onChange={(event) => onChange(event.target.value)} placeholder={`${placeholder} · one per line · use | for detail`} disabled={disabled} /></label>; }
function Note({
  label, value }: { label: string; value?: string | null }) { return <div><span>{label}</span><p>{value || "Not set yet"}</p></div>; }
function AssetChip({
  asset,
}: { asset: MovieWorldAsset }) { return <div className="movie-world-asset-chip"><ImageIcon size={14} aria-hidden="true" /><span><small>Reference asset</small><strong>{asset.name}</strong></span><Link href={`/assets?search=${encodeURIComponent(asset.name)}`}><ArrowUpRight size={13} /></Link></div>; }
function ReferenceCard({
  reference, assets }: { reference: MovieWorldReference; assets: MovieWorldAsset[] }) { const asset = assetLabel(reference.assetId, assets); return <Link href={asset ? `/assets?search=${encodeURIComponent(asset.name)}` : "#"} className="movie-world-reference"><span className="movie-world-reference-mark"><ImageIcon size={16} aria-hidden="true" /></span><span><strong>{reference.name}</strong><small>{reference.kind}{asset ? ` · ${asset.name}` : " · No Asset linked"}</small></span><ArrowUpRight size={13} /></Link>; }
function XCircleIcon() { return <span className="movie-error-icon" aria-hidden="true">!</span>; }
