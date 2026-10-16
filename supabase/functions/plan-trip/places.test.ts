// Run: node --test supabase/functions/plan-trip/places.test.ts   (no network: Photon / Overpass are faked)
import { test } from "node:test";
import assert from "node:assert/strict";
import { distanceKm, foodVenues, isPinnable, locate, renameIn, sameName, verifyPlan, withoutGenericWords, type Fetch, type Venue } from "./places.ts";
import type { PlanPlace, TripPlan } from "./planner.ts";

const VAN = { lat: 38.5012, lon: 43.3730 };

const feature = (name: string, lat: number, lon: number, kind = "tourism:attraction") => ({
  properties: { name, osm_key: kind.split(":")[0], osm_value: kind.split(":")[1] },
  geometry: { coordinates: [lon, lat] },
});

/** Photon answers by query text; Overpass answers with the given elements. */
function fakeMap(photon: Record<string, unknown[]>, overpass: unknown[] = []) {
  const queries: string[] = [];
  const fetch: Fetch = async (url) => {
    if (url.startsWith("https://overpass")) return new Response(JSON.stringify({ elements: overpass }));
    const q = new URL(url).searchParams.get("q") ?? "";
    queries.push(q);
    return new Response(JSON.stringify({ features: photon[q] ?? [] }));
  };
  return { fetch, queries };
}

const place = (key: string, name: string, extra: Partial<PlanPlace> = {}): PlanPlace => ({
  key, name, localName: null, area: null, category: "landmark", description: "", lat: null, lon: null, visitMinutes: null, ...extra,
});

test("names match on whole words, without case or accents", () => {
  assert.ok(sameName("Van Kalesi", "Van Kalesi Höyüğü"));
  assert.ok(sameName("Akdamar Kilisesi", "AKDAMAR KİLİSESİ"));
  assert.ok(!sameName("Van", "Van Gölü"));          // too short to trust
  assert.ok(!sameName("Roma", "Romantik Otel"));
  assert.ok(distanceKm(VAN, { lat: 38.3436, lon: 43.0333 }) > 30); // Akdamar is ~35 km from the centre
});

test("a swapped name fits the activity's text", () => {
  assert.equal(renameIn("Hacı Baba'da öğle yemeği", "Hacı Baba", "Sütçü Kenan"), "Öğle yemeği – Sütçü Kenan");
  assert.equal(renameIn("Eski Müze gezisi", "Eski Müze", "Van Müzesi"), "Van Müzesi gezisi");
  assert.equal(renameIn("Hacı Baba'da", "Hacı Baba", "Sütçü Kenan"), "Sütçü Kenan");
  assert.equal(renameIn(null, "a", "b"), null);
});

test("a place is found by name near the destination, not by a namesake far away", async () => {
  const { fetch } = fakeMap({
    "Van Kalesi": [feature("Van Kalesi", 40.0, 30.0), feature("Van Kalesi", 38.5030, 43.3420)],
  });
  const pt = await locate(fetch, place("p1", "Van Kalesi"), VAN, []);
  assert.deepEqual(pt, { lat: 38.5030, lon: 43.3420 });
});

test("the local name is tried first; the model's guessed position is never used as a pin", async () => {
  const { fetch, queries } = fakeMap({
    "Colosseo": [],
    "Kolezyum": [feature("Anfiteatro Flavio", 41.8902, 12.4922)], // right at the guess, but another name
  });
  const rome = { lat: 41.9028, lon: 12.4964 };
  const pt = await locate(fetch, place("p1", "Kolezyum", { localName: "Colosseo", lat: 41.8905, lon: 12.4920 }), rome, []);
  assert.deepEqual(queries, ["Colosseo", "Kolezyum"]);
  assert.equal(pt, null);
});

test("descriptive words are dropped: 'Erciş Şehri Merkezi' is the town of Erciş, not the model's guess in the lake", async () => {
  const { fetch, queries } = fakeMap({
    "Erciş": [feature("Erciş Kalesi", 39.02, 43.35), feature("Erciş", 39.0283, 43.3589, "place:town")],
  });
  const pt = await locate(fetch, place("p2", "Erciş Şehri Merkezi", { lat: 38.60, lon: 42.95 }), VAN, []);
  assert.deepEqual(queries, ["Erciş Şehri Merkezi", "Erciş"]);
  assert.deepEqual(pt, { lat: 39.0283, lon: 43.3589 }); // the exact name wins over "Erciş Kalesi"
});

test("the middle of a lake or a whole region is no pin", async () => {
  const { fetch } = fakeMap({
    "Van Gölü Kıyı Şeridi": [],
    "Van Gölü": [feature("Van Gölü", 38.63, 42.90, "natural:water")],
  });
  assert.equal(await locate(fetch, place("p3", "Van Gölü Kıyı Şeridi", { category: "nature" }), VAN, []), null);
  assert.equal(withoutGenericWords("Van Gölü Kıyı Şeridi"), "Van Gölü");
  assert.ok(isPinnable("place:town") && isPinnable("tourism:attraction") && !isPinnable("place:province"));
});

test("restaurants come from the map: named, not chains, well-documented first", async () => {
  const { fetch } = fakeMap({}, [
    { type: "node", lat: 38.50, lon: 43.38, tags: { amenity: "restaurant", name: "Sütçü Kenan", cuisine: "breakfast", website: "x", wikidata: "Q1" } },
    { type: "node", lat: 38.49, lon: 43.37, tags: { amenity: "restaurant", name: "Burger Zinciri", brand: "Burger Zinciri" } },
    { type: "way", center: { lat: 38.51, lon: 43.36 }, tags: { amenity: "cafe", name: "Göl Kafe" } },
    { type: "node", lat: 38.5, lon: 43.4, tags: { amenity: "restaurant" } },
  ]);
  const venues = await foodVenues(fetch, VAN);
  assert.deepEqual(venues.map((v) => v.name), ["Sütçü Kenan", "Göl Kafe"]);
  assert.equal(venues[1].kind, "cafe");
  assert.equal(venues[1].lat, 38.51);
});

function samplePlan(): TripPlan {
  return {
    summary: "Van",
    places: [
      place("p1", "Van Kalesi"),
      place("p2", "Hayali Kahvaltı Evi", { category: "restaurant" }),
      place("p3", "Uydurma Müze", { category: "museum" }),
      place("p4", "Sütçü Kenan", { category: "restaurant" }),
      place("p5", "Hiçbir Yerde", { category: "viewpoint" }),
    ],
    days: [{
      day: 1, title: "Van", activities: [
        { start: "09:00", end: "10:00", title: "Hayali Kahvaltı Evi'nde Van kahvaltısı", note: null, placeKey: "p2" },
        { start: "10:30", end: "12:30", title: "Van Kalesi", note: null, placeKey: "p1" },
        { start: "13:00", end: "14:00", title: "Uydurma Müze gezisi", note: "Uydurma Müze pazartesi kapalı", placeKey: "p3" },
        { start: "14:30", end: "15:30", title: "Sütçü Kenan'da öğle yemeği", note: null, placeKey: "p4" },
        { start: "17:00", end: "18:00", title: "Gün batımı", note: null, placeKey: "p5" },
      ],
    }],
    todos: [], tips: [],
  };
}

test("every place ends up on the map: found, swapped for a real venue, replaced by the model, or dropped", async () => {
  const venues: Venue[] = [
    { name: "Sütçü Kenan", kind: "restaurant", cuisine: "breakfast", lat: 38.50, lon: 43.38 },
    { name: "Kahvaltıcı Kemal", kind: "restaurant", cuisine: null, lat: 38.503, lon: 43.345 },
    { name: "Uzaktaki Lokanta", kind: "restaurant", cuisine: null, lat: 38.60, lon: 43.50 },
  ];
  const { fetch } = fakeMap({
    "Van Kalesi": [feature("Van Kalesi", 38.5030, 43.3420)],
    "Van Müzesi": [feature("Van Müzesi", 38.4950, 43.3800, "tourism:museum")],
  });
  const asked: string[][] = [];
  const replacements = async (missing: PlanPlace[]) => {
    asked.push(missing.map((m) => m.key));
    return [
      place("p3", "Van Müzesi", { category: "museum", description: "Urartu eserleri" }),
      place("p5", "Yine Uydurma Tepe", { category: "viewpoint" }),
    ];
  };

  const result = await verifyPlan(samplePlan(), VAN, venues, fetch, replacements);

  assert.deepEqual(asked, [["p3", "p5"]]); // the food place was swapped without asking
  assert.deepEqual(result.places.map((p) => [p.key, p.name, p.verified]), [
    ["p1", "Van Kalesi", true],
    ["p2", "Kahvaltıcı Kemal", true],       // the unused venue nearest to that day's other places
    ["p3", "Van Müzesi", true],
    ["p4", "Sütçü Kenan", true],
  ]);
  assert.equal(result.places[1].lat, 38.503);
  assert.equal(result.replaced, 2);
  assert.equal(result.dropped, 1);

  const acts = result.days[0].activities;
  assert.equal(acts[0].title, "Van kahvaltısı – Kahvaltıcı Kemal");   // name swapped in the title
  assert.equal(acts[2].title, "Van Müzesi gezisi");
  assert.equal(acts[2].note, "Van Müzesi pazartesi kapalı");
  assert.equal(acts[4].placeKey, null);      // nowhere on the map: the activity stays, without a pin
  assert.equal(acts[4].title, "Gün batımı");
});

test("a map service that doesn't answer is not 'not on the map': those places go to the app unchecked", async () => {
  let calls = 0;
  const fetch: Fetch = async () => { calls++; throw new Error("TimeoutError: Signal timed out."); };
  const asked: string[] = [];
  const result = await verifyPlan(samplePlan(), VAN, [], fetch, async (m) => { asked.push(...m.map((p) => p.key)); return []; });

  assert.equal(result.places.length, 5);                       // nothing dropped, swapped or replaced
  assert.ok(result.places.every((p) => !p.verified));
  assert.equal(result.unchecked, 5);
  assert.equal(result.dropped, 0);
  assert.deepEqual(asked, []);
  assert.equal(result.days[0].activities[1].placeKey, "p1");   // still linked
  assert.ok(calls >= 10);                                       // each lookup was tried twice
});

test("the map check stops at the deadline and leaves the rest to the app", async () => {
  const { fetch } = fakeMap({ "Van Kalesi": [feature("Van Kalesi", 38.503, 43.342)] });
  const result = await verifyPlan(samplePlan(), VAN, [], fetch, null, Date.now() - 1);
  assert.equal(result.unchecked, 5);
  assert.equal(result.dropped, 0);
});
