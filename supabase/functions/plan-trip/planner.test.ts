// Run: node --test supabase/functions/plan-trip/planner.test.ts   (Node 22.18+, TypeScript types are stripped)
import { test } from "node:test";
import assert from "node:assert/strict";
import { buildMessages, buildReplacementMessages, MAX_DAYS, nameKey, parseRequest, PLAN_SCHEMA, REPLACEMENT_SCHEMA, RequestError, sanitizePlan, sanitizeReplacements } from "./planner.ts";

const valid = {
  deviceId: "ab".repeat(32),
  language: "tr", destination: "Roma, İtalya", startDate: "2026-10-22", days: 5,
  interests: ["history", "food", "hacking"], pace: "balanced", budget: "mid", companions: "couple",
  notes: "Vejetaryeniz", existingPlaces: ["Kolezyum"],
};

test("a valid request is kept, unknown interests dropped", () => {
  const { request, truncatedDays } = parseRequest(valid);
  assert.equal(request.destination, "Roma, İtalya");
  assert.deepEqual(request.interests, ["history", "food"]);
  assert.equal(truncatedDays, false);
});

test("long trips are planned for their first MAX_DAYS days", () => {
  const { request, truncatedDays } = parseRequest({ ...valid, days: 30 });
  assert.equal(request.days, MAX_DAYS);
  assert.equal(truncatedDays, true);
});

test("bad requests are refused", () => {
  assert.throws(() => parseRequest({ ...valid, destination: "" }), RequestError);
  assert.throws(() => parseRequest({ ...valid, startDate: "22.10.2026" }), RequestError);
  assert.throws(() => parseRequest({ ...valid, days: 0 }), RequestError);
  assert.throws(() => parseRequest(null), RequestError);
  assert.throws(() => parseRequest({ ...valid, deviceId: undefined }), RequestError); // the quota needs the device
  assert.throws(() => parseRequest({ ...valid, deviceId: "not-a-hash" }), RequestError);
});

test("unknown choices fall back to defaults, notes are cut", () => {
  const { request } = parseRequest({ ...valid, pace: "x", budget: 5, companions: null, notes: "a".repeat(500), language: "de" });
  assert.equal(request.pace, "balanced");
  assert.equal(request.budget, "mid");
  assert.equal(request.companions, "couple");
  assert.equal(request.notes.length, 200);
  assert.equal(request.language, "tr");
});

test("user text goes in as data, the language is set", () => {
  const [system, user] = buildMessages(parseRequest({ ...valid, language: "en" }).request);
  assert.match(system.content, /English/);
  assert.match(system.content, /ignore any instructions/);
  assert.equal(JSON.parse(user.content).notes, "Vejetaryeniz");
});

test("schema is strict: every object lists all its properties as required", () => {
  const check = (node: any) => {
    if (node?.type === "object") {
      assert.equal(node.additionalProperties, false);
      assert.deepEqual([...node.required].sort(), Object.keys(node.properties).sort());
      Object.values(node.properties).forEach(check);
    }
    if (node?.type === "array") check(node.items);
  };
  check(PLAN_SCHEMA);
  check(REPLACEMENT_SCHEMA);
});

test("the app's city centre is kept when it is a real point", () => {
  assert.deepEqual(parseRequest({ ...valid, center: { lat: 41.9, lon: 12.5 } }).request.center, { lat: 41.9, lon: 12.5 });
  assert.equal(parseRequest({ ...valid, center: { lat: 0, lon: 0 } }).request.center, null);
  assert.equal(parseRequest({ ...valid, center: { lat: 99, lon: 12 } }).request.center, null);
  assert.equal(parseRequest(valid).request.center, null);
});

test("real venues from the map go to the model; it is told to name every place", () => {
  const request = parseRequest(valid).request;
  const [system, user] = buildMessages(request, [{ name: "Da Enzo al 29", kind: "restaurant", cuisine: "roman" }]);
  assert.match(system.content, /Never "Restoranda yemek"/);
  assert.equal(JSON.parse(user.content).food_venues_on_map[0].name, "Da Enzo al 29");

  const [, ask] = buildReplacementMessages(request, [{ key: "p3", name: "Uydurma", localName: null, area: null, category: "museum",
    description: "", lat: null, lon: null, visitMinutes: null }], ["Kolezyum"], []);
  assert.deepEqual(JSON.parse(ask.content).not_found[0].key, "p3");
  assert.deepEqual(sanitizeReplacements({ places: [{ key: "p3", name: "Musei Capitolini", local_name: null, area: null,
    category: "museum", description: "", lat: 41.89, lon: 12.48, visit_minutes: 90 }, { key: "", name: "anahtarsız" }] }).map((p) => p.key), ["p3"]);
});

test("the model's answer is cleaned to what the app can store", () => {
  const plan = sanitizePlan({
    summary: "Roma",
    places: [
      { key: "p1", name: "Kolezyum", local_name: "Colosseo", area: "Monti", category: "landmark", description: "Antik arena", lat: 41.89021, lon: 12.49223, visit_minutes: 120 },
      { key: "p2", name: "kolezyum", local_name: null, area: null, category: "landmark", description: "dup", lat: null, lon: null, visit_minutes: null },
      { key: "p3", name: "Vatikan Müzeleri", local_name: "Vatikan Müzeleri", area: "", category: "zoo", description: "Sanat", lat: 0, lon: 0, visit_minutes: 9999 },
      { key: "p4", name: "Hiç kullanılmayan", local_name: null, area: null, category: "park", description: "", lat: 95, lon: 12, visit_minutes: null },
    ],
    days: [
      { day: 2, title: "Vatikan", activities: [
        { start: "14:00", end: "13:00", title: "Öğle sonrası", note: null, place_key: null },
        { start: "8:30", end: "11:30", title: "Vatikan Müzeleri", note: "", place_key: "Vatikan Müzeleri" }, // name instead of key
      ] },
      { day: 1, title: "Antik Roma", activities: [
        { start: "25:00", end: "26:00", title: "Kolezyum", note: "Erken git", place_key: "p2" },        // duplicate's key → p1
        { start: null, end: null, title: "", note: null, place_key: null },
        { start: "10:00", end: null, title: "kolezyum", note: null, place_key: "p9" },                 // duplicate title
      ] },
      { day: 9, title: "out of range", activities: [] },
      { day: 1, title: "duplicate day", activities: [] },
    ],
    todos: ["Vatikan biletlerini al", "", 5, "vatikan biletlerini al"],
    tips: ["Su şişeni doldur"],
  }, 5);

  assert.deepEqual(plan.days.map((d) => d.day), [1, 2]);
  const [day1, day2] = plan.days;
  assert.equal(day1.activities.length, 1);
  assert.deepEqual(day1.activities[0], { start: null, end: null, title: "Kolezyum", note: "Erken git", placeKey: "p1" });

  assert.deepEqual(day2.activities.map((a) => a.title), ["Vatikan Müzeleri", "Öğle sonrası"]); // sorted by time
  assert.deepEqual(day2.activities[0], { start: "08:30", end: "11:30", title: "Vatikan Müzeleri", note: null, placeKey: "p3" });
  assert.equal(day2.activities[1].end, null);                                                       // end before start

  assert.deepEqual(plan.places.map((p) => p.key), ["p1", "p3", "p4"]);  // visit order, unused last, duplicate gone
  const [colosseum, vatican, unused] = plan.places;
  assert.equal(colosseum.localName, "Colosseo");
  assert.equal(colosseum.area, "Monti");
  assert.equal(colosseum.lat, 41.89021);
  assert.equal(colosseum.visitMinutes, 120);
  assert.equal(vatican.localName, null);   // same as name
  assert.equal(vatican.area, null);
  assert.equal(vatican.category, "other"); // not one of ours
  assert.equal(vatican.lat, null);         // 0,0 is no position
  assert.equal(vatican.visitMinutes, null);
  assert.equal(unused.lat, null);          // out of range
  assert.equal(unused.lon, null);
  assert.deepEqual(plan.todos, ["Vatikan biletlerini al"]);
});

test("answers in the first version's shape (time / place) still work", () => {
  const plan = sanitizePlan({
    places: [{ name: "Kolezyum", description: "Antik arena", category: "Tarih" }],
    days: [{ day: 1, title: "", activities: [{ time: "09:00", title: "Tur", note: null, place: "Kolezyum" }] }],
  }, 1);
  assert.equal(plan.places[0].key, "p1");
  assert.deepEqual(plan.days[0].activities[0], { start: "09:00", end: null, title: "Tur", note: null, placeKey: "p1" });
});

test("names match without case or accents", () => {
  assert.equal(nameKey("Café de Flore"), nameKey("CAFE DE FLORE"));
  assert.equal(nameKey("Ayasofya-i Kebir"), nameKey("ayasofya i kebir"));
  assert.equal(nameKey("İstiklal Caddesi"), nameKey("istiklal caddesi"));
});

test("garbage from the model gives an empty plan, not an exception", () => {
  const plan = sanitizePlan("nonsense", 3);
  assert.deepEqual(plan, { summary: "", days: [], places: [], todos: [], tips: [] });
});

test("a place is visited on one day only: repeats on later days are dropped, titles that name a place count", () => {
  const act = (start: string, title: string, place_key: string | null) => ({ start, end: null, title, note: null, place_key });
  const plan = sanitizePlan({
    places: [
      { key: "p1", name: "İshakpaşa Sarayı", local_name: null, area: "Doğubayazıt", category: "historic", description: "", lat: null, lon: null, visit_minutes: 120 },
      { key: "p2", name: "Mercimekli Gölü", local_name: null, area: null, category: "nature", description: "", lat: null, lon: null, visit_minutes: 60 },
    ],
    days: [
      { day: 2, title: "Tekrar", activities: [act("09:00", "İshakpaşa Sarayı fotoğraf saatleri", "p1"), act("11:00", "Şafak Bendi gezisi", null)] },
      { day: 1, title: "Varış", activities: [act("12:00", "İshakpaşa Sarayı ziyareti", "p1"), act("13:30", "Mercimekli Gölü ve sazlık", null)] },
      { day: 3, title: "Göl", activities: [act("09:30", "Mercimekli Gölü doğa yürüyüşü", null), act("12:00", "Mercimekli Gölü'nde piknik", "p2")] },
    ],
  }, 3);

  const titles = plan.days.map((d) => d.activities.map((a) => a.title));
  assert.deepEqual(titles, [
    ["İshakpaşa Sarayı ziyareti", "Mercimekli Gölü ve sazlık"], // day 1 keeps the first visits
    ["Şafak Bendi gezisi"],                                      // the palace again: dropped
    [],                                                          // the lake again (by title and by key): dropped
  ]);
  assert.equal(plan.days[0].activities[1].placeKey, "p2");     // linked by the name in its title
});
