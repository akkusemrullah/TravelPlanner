// TravelPlanner - AI trip planner: grounding the plan on the map (OpenStreetMap).
//
// 1. foodVenues: real restaurants / cafes around the destination (Overpass API) are given to the model, so meals
//    happen at named venues that exist on the map.
// 2. verifyPlan: every place of the plan is looked up on the map (Photon geocoder / the venue list). Places that
//    can't be found: a food place is swapped for a real venue nearby; the others go back to the model once for a
//    real replacement; whatever still can't be found is dropped (its activities keep their text, without a pin).
//
// Network calls go through an injected fetch so the logic can be tested with plain Node (places.test.ts).

import { nameKey, type PlanPlace, type TripPlan } from "./planner.ts";

export type Fetch = (url: string, init?: RequestInit) => Promise<Response>;
export interface Point { lat: number; lon: number }

/** A real food place from OpenStreetMap, offered to the model. */
export interface Venue { name: string; kind: string; cuisine: string | null; lat: number; lon: number }

/** A place of the plan with what the map said about it. */
export interface VerifiedPlace extends PlanPlace { verified: boolean }
export interface VerifiedPlan extends Omit<TripPlan, "places"> { places: VerifiedPlace[] }

/** Asks the model for real replacements of places that weren't found (same keys back). */
export type AskReplacements = (missing: PlanPlace[], used: string[], venues: Venue[]) => Promise<PlanPlace[]>;

const PHOTON = "https://photon.komoot.io/api/";
/** Overpass servers, tried in turn (the main one sometimes refuses cloud servers). */
const OVERPASS = [
  "https://overpass-api.de/api/interpreter",
  "https://overpass.private.coffee/api/interpreter",
  "https://maps.mail.ru/osm/tools/overpass/api/interpreter",
];
const HEADERS = { "User-Agent": "TravelPlanner-plan-trip/1.1 (trip planner app)" };

/** A match farther than this from the destination is another place with the same name (day trips included). */
export const MAX_DISTANCE_KM = 120;
/** Restaurants / cafes searched this far around the destination. */
export const VENUE_RADIUS_M = 6000;
export const MAX_VENUES = 40;

export const FOOD_CATEGORIES = new Set(["restaurant", "cafe", "market"]);

export function distanceKm(a: Point, b: Point): number {
  const rad = (d: number) => (d * Math.PI) / 180;
  const dLat = rad(b.lat - a.lat), dLon = rad(b.lon - a.lon);
  const h = Math.sin(dLat / 2) ** 2 + Math.cos(rad(a.lat)) * Math.cos(rad(b.lat)) * Math.sin(dLon / 2) ** 2;
  return 6371 * 2 * Math.atan2(Math.sqrt(h), Math.sqrt(1 - h));
}

/** "Colosseo" fits "Colosseo"; "Van Kalesi" fits "Van Kalesi Höyüğü" (whole words, at least 4 letters). */
export function sameName(a: string, b: string): boolean {
  const x = nameKey(a), y = nameKey(b);
  if (!x || !y) return false;
  if (x === y) return true;
  const [shorter, longer] = x.length <= y.length ? [x, y] : [y, x];
  return shorter.length >= 4 && ` ${longer} `.includes(` ${shorter} `);
}

/** A map service that couldn't be reached (timeout, refused, overloaded) - not the same as "not on the map". */
export const UNREACHABLE = Symbol("unreachable");

/** GET/POST with a timeout and one retry for timeouts and overloaded servers. */
async function getJson(fetch: Fetch, url: string, init: RequestInit = {}, timeoutMs = 6000): Promise<any | typeof UNREACHABLE> {
  for (let attempt = 1; ; attempt++) {
    try {
      const res = await fetch(url, {
        ...init,
        headers: { ...HEADERS, Accept: "application/json", ...(init.headers ?? {}) },
        signal: AbortSignal.timeout(timeoutMs),
      });
      if (res.ok) return await res.json();
      console.warn("map lookup failed", res.status, url.slice(0, 120));
      if (attempt >= 2 || !(res.status === 429 || res.status >= 500)) return UNREACHABLE;
    } catch (e) {
      console.warn("map lookup failed", String(e), url.slice(0, 120));
      if (attempt >= 2) return UNREACHABLE;
    }
    await new Promise((r) => setTimeout(r, 600 * attempt));
  }
}

interface Feature { name: string; lat: number; lon: number; kind: string }

function features(body: any): Feature[] {
  return (Array.isArray(body?.features) ? body.features : [])
    .map((f: any) => ({
      name: String(f?.properties?.name ?? ""),
      lon: Number(f?.geometry?.coordinates?.[0]),
      lat: Number(f?.geometry?.coordinates?.[1]),
      kind: `${f?.properties?.osm_key ?? ""}:${f?.properties?.osm_value ?? ""}`,
    }))
    .filter((f: Feature) => f.name && Number.isFinite(f.lat) && Number.isFinite(f.lon));
}

/** Features for a query; null when Photon couldn't be reached. */
async function photon(fetch: Fetch, query: string, near: Point | null, extra = ""): Promise<Feature[] | null> {
  const bias = near ? `&lat=${near.lat}&lon=${near.lon}` : "";
  const body = await getJson(fetch, `${PHOTON}?q=${encodeURIComponent(query)}&limit=10${bias}${extra}`);
  return body === UNREACHABLE ? null : features(body);
}

/** The destination's centre ("Van, Türkiye"), when the app didn't send it. */
export async function findCenter(fetch: Fetch, destination: string): Promise<Point | null> {
  const found = (await photon(fetch, destination, null, "&osm_tag=place")) ?? [];
  const best = found.find((f) => /:(city|town|village|municipality|county|state|province|island)$/.test(f.kind)) ?? found[0];
  return best ? { lat: best.lat, lon: best.lon } : null;
}

/** Named restaurants / cafes around the centre; the ones with more details on the map (well known) first. */
export async function foodVenues(fetch: Fetch, center: Point): Promise<Venue[]> {
  const query = `[out:json][timeout:12];
nwr(around:${VENUE_RADIUS_M},${center.lat},${center.lon})[amenity~"^(restaurant|cafe)$"][name];
out center tags 300;`;
  let body: any = UNREACHABLE;
  for (const server of OVERPASS) {
    body = await getJson(fetch, server, {
      method: "POST",
      headers: { "Content-Type": "application/x-www-form-urlencoded" },
      body: `data=${encodeURIComponent(query)}`,
    }, 12000);
    if (body !== UNREACHABLE) break;
  }
  if (body === UNREACHABLE) return [];

  const seen = new Set<string>();
  const scored: (Venue & { score: number })[] = [];
  for (const el of Array.isArray(body?.elements) ? body.elements : []) {
    const t = el?.tags ?? {};
    const name = String(t.name ?? "").trim();
    const lat = Number(el.lat ?? el.center?.lat), lon = Number(el.lon ?? el.center?.lon);
    if (!name || !Number.isFinite(lat) || !Number.isFinite(lon) || seen.has(nameKey(name))) continue;
    if (t.brand || t["brand:wikidata"]) continue; // chains (fast food, coffee chains) are not what a trip plan wants
    seen.add(nameKey(name));
    const score = (t.wikidata ? 4 : 0) + (t.website || t["contact:website"] ? 2 : 0) + (t.cuisine ? 1 : 0) +
      (t.opening_hours ? 1 : 0) + (t.phone || t["contact:phone"] ? 1 : 0) - distanceKm(center, { lat, lon }) / 10;
    scored.push({
      name,
      kind: t.amenity === "cafe" ? "cafe" : "restaurant",
      cuisine: t.cuisine ? String(t.cuisine).slice(0, 40) : null,
      lat: Math.round(lat * 1e5) / 1e5,
      lon: Math.round(lon * 1e5) / 1e5,
      score,
    });
  }
  return scored.sort((a, b) => b.score - a.score).slice(0, MAX_VENUES).map(({ score: _, ...v }) => v);
}

/**
 * Descriptive words the model adds to a real name ("Erciş Şehri Merkezi", "Van Gölü Kıyı Şeridi", "Old Town
 * waterfront"): the map knows "Erciş", not "Erciş Şehri Merkezi". Searched without them too.
 */
const GENERIC_WORDS = [
  "şehir merkezi", "şehri merkezi", "ilçe merkezi", "ilçesi merkezi", "kent merkezi", "merkezi", "kıyı şeridi",
  "sahil şeridi", "kıyısı", "sahili", "çevresi", "bölgesi", "city centre", "city center", "town centre",
  "town center", "downtown", "waterfront", "shoreline", "promenade area", "area",
];

export function withoutGenericWords(name: string): string {
  let out = ` ${name} `;
  for (const w of GENERIC_WORDS) out = out.replace(new RegExp(`\\s${w}(?=\\s)`, "giu"), " ");
  return out.replace(/\s{2,}/g, " ").trim();
}

/**
 * Map features that are no place to pin a visit on: the middle of a lake or sea, a whole region or country.
 * (A town or a district is fine - a day trip "to Erciş" is pinned on Erciş.)
 */
export function isPinnable(kind: string): boolean {
  return !/^(natural:(water|bay|strait|lake|sea|wetland|peninsula|mountain_range)|water(way)?:|place:(sea|ocean|state|region|province|country|county|continent|archipelago)|boundary:|landuse:)/.test(kind);
}

/**
 * The place's position on the map; null when the map doesn't know it; UNREACHABLE when the map service didn't
 * answer (or time ran out), so nobody can tell. The model's own guess is never used as a position.
 */
export async function locate(
  fetch: Fetch, place: PlanPlace, center: Point | null, venues: Venue[], deadline = Infinity,
): Promise<Point | null | typeof UNREACHABLE> {
  const given = [place.localName, place.name].filter((n): n is string => !!n);
  const names = [...new Set([...given, ...given.map(withoutGenericWords)].filter((n) => nameKey(n).length >= 3))];

  // a venue from the list we gave the model
  const venue = venues.find((v) => names.some((n) => nameKey(n) === nameKey(v.name)));
  if (venue) return { lat: venue.lat, lon: venue.lon };

  // the guess only helps to rank namesakes, it is never a position by itself
  const hint = place.lat !== null && place.lon !== null ? { lat: place.lat, lon: place.lon } : null;
  const anchor = hint && (!center || distanceKm(center, hint) <= MAX_DISTANCE_KM) ? hint : center;

  let unreachable = false;
  for (const query of names) {
    if (Date.now() > deadline) return UNREACHABLE;
    const answer = await photon(fetch, query, anchor);
    if (answer === null) {
      unreachable = true;
      continue;
    }
    const found = answer
      .filter((f) => isPinnable(f.kind))
      .filter((f) => !center || distanceKm(center, f) <= MAX_DISTANCE_KM)
      .filter((f) => names.some((n) => sameName(n, f.name)))
      .map((f) => ({
        f,
        exact: names.some((n) => nameKey(n) === nameKey(f.name)) ? 0 : 1,
        km: anchor ? distanceKm(anchor, f) : 0,
      }))
      .sort((a, b) => a.exact - b.exact || a.km - b.km);

    if (found[0]) return { lat: found[0].f.lat, lon: found[0].f.lon };
  }
  return unreachable ? UNREACHABLE : null;
}

/** Runs fn over items, at most `limit` at a time (be gentle with the free map services). */
async function mapLimited<T, R>(items: T[], limit: number, fn: (item: T) => Promise<R>): Promise<R[]> {
  const results = new Array<R>(items.length);
  let next = 0;
  const workers = Array.from({ length: Math.min(limit, items.length) }, async () => {
    while (next < items.length) {
      const i = next++;
      results[i] = await fn(items[i]);
    }
  });
  await Promise.all(workers);
  return results;
}

function escapeRegExp(s: string) {
  return s.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

/**
 * The old place's name swapped for the new one in an activity's text. A Turkish suffix after an apostrophe
 * ("Hacı Baba'da kahvaltı") would not fit the new name, so then the name moves to the end:
 * "Kahvaltı – Sütçü Kenan". Without a suffix the name is simply replaced ("Eski Müze gezisi" → "Van Müzesi gezisi").
 */
export function renameIn(text: string | null, from: string, to: string): string | null {
  if (!text || !from || nameKey(from) === nameKey(to)) return text;
  const withSuffix = new RegExp(`${escapeRegExp(from)}['’]\\p{L}*`, "giu");
  if (withSuffix.test(text)) {
    const rest = text.replace(withSuffix, " ").replace(/\s{2,}/g, " ").trim();
    return rest ? `${rest.charAt(0).toLocaleUpperCase("tr")}${rest.slice(1)} – ${to}` : to;
  }
  return text.replace(new RegExp(escapeRegExp(from), "gi"), to);
}

/**
 * Checks every place of the plan on the map (see the top of this file). Verified places carry the map's
 * coordinates; the result has no unverified places left.
 */
export async function verifyPlan(
  plan: TripPlan, center: Point | null, venues: Venue[], fetch: Fetch, askReplacements: AskReplacements | null,
  deadline = Infinity,
): Promise<VerifiedPlan & { replaced: number; dropped: number; unchecked: number }> {
  const places: PlanPlace[] = plan.places.map((p) => ({ ...p }));
  const days = plan.days.map((d) => ({ ...d, activities: d.activities.map((a) => ({ ...a })) }));
  const found = new Map<string, Point>();
  // places the map service didn't answer for: not swapped, replaced or dropped - the app checks them itself
  const unchecked = new Set<string>();
  let replaced = 0;

  const check = async (list: PlanPlace[]) => {
    const points = await mapLimited(list, 3, (p) => locate(fetch, p, center, venues, deadline));
    list.forEach((p, i) => {
      const pt = points[i];
      if (pt === UNREACHABLE) unchecked.add(p.key);
      else if (pt) found.set(p.key, pt);
    });
  };
  const missingOnMap = (p: PlanPlace) => !found.has(p.key) && !unchecked.has(p.key);

  const swap = (old: PlanPlace, now: PlanPlace) => {
    for (const day of days) {
      for (const a of day.activities) {
        if (a.placeKey !== old.key) continue;
        for (const from of [old.name, old.localName]) {
          if (!from) continue;
          a.title = renameIn(a.title, from, now.name)!;
          a.note = renameIn(a.note, from, now.name);
        }
      }
    }
    Object.assign(old, { ...now, key: old.key });
    replaced++;
  };

  await check(places);

  // food places that aren't on the map → a real venue near the rest of that day
  const usedVenues = new Set(places.map((p) => nameKey(p.name)));
  for (const p of places.filter((p) => missingOnMap(p) && FOOD_CATEGORIES.has(p.category))) {
    const day = days.find((d) => d.activities.some((a) => a.placeKey === p.key));
    const near = day?.activities.map((a) => a.placeKey && found.get(a.placeKey)).find((pt): pt is Point => !!pt) ?? center;
    const venue = venues
      .filter((v) => !usedVenues.has(nameKey(v.name)) && (p.category !== "cafe" || v.kind === "cafe"))
      .sort((a, b) => (near ? distanceKm(near, a) - distanceKm(near, b) : 0))[0];
    if (!venue) continue;
    usedVenues.add(nameKey(venue.name));
    const kind = venue.kind === "cafe" ? "cafe" : "restaurant";
    // the old description was about the other venue
    swap(p, { ...p, name: venue.name, localName: null, area: null, category: kind, description: "", lat: venue.lat, lon: venue.lon });
    found.set(p.key, { lat: venue.lat, lon: venue.lon });
  }

  // the rest: one more question to the model, then check its answers too
  const missing = places.filter(missingOnMap);
  if (missing.length > 0 && askReplacements && Date.now() < deadline - 40_000) { // the question needs up to ~35 s
    try {
      const answers = await askReplacements(missing, places.filter((p) => found.has(p.key)).map((p) => p.name), venues);
      for (const m of missing) {
        const answer = answers.find((a) => a.key === m.key && a.name && nameKey(a.name) !== nameKey(m.name));
        if (!answer || places.some((p) => p !== m && nameKey(p.name) === nameKey(answer.name))) continue;
        const candidate = { ...answer, key: m.key };
        const point = await locate(fetch, candidate, center, venues, deadline);
        if (!point || point === UNREACHABLE) continue;
        swap(m, candidate);
        found.set(m.key, point);
      }
    } catch (e) {
      console.warn("replacement request failed", String(e));
    }
  }

  // whatever is still unknown is dropped; its activities stay, without a place
  const dropped = places.filter(missingOnMap).map((p) => p.key);
  for (const day of days) {
    for (const a of day.activities) if (a.placeKey && dropped.includes(a.placeKey)) a.placeKey = null;
  }

  return {
    ...plan,
    days,
    places: places.filter((p) => found.has(p.key) || unchecked.has(p.key)).map((p) => {
      const pt = found.get(p.key);
      return pt ? { ...p, lat: pt.lat, lon: pt.lon, verified: true } : { ...p, verified: false };
    }),
    replaced,
    dropped: dropped.length,
    unchecked: unchecked.size,
  };
}
