-- TravelPlanner - TP-33: three sample trips for Keşfet, added as DRAFTS (nobody sees them yet).
-- Run in the SQL Editor after migration 23, review the content and coordinates, then publish each one:
--   select private.explore_set_status('istanbul-3-gun', 'published');
-- Not part of all_in_one.sql on purpose (content, not schema). Safe to run again (matched by slug; an update keeps
-- the status). Format: docs/EXPLORE_CONTENT.md. Images: upload covers/<slug>.webp and thumbs/<slug>.webp to the
-- explore-images bucket and add cover_path / thumb_path / image_credit.

select private.explore_upsert($json${
  "slug": "istanbul-3-gun",
  "title": "İstanbul 3 gün",
  "summary": "Tarihi yarımada, Boğaz ve Beyoğlu: ilk kez gidenler için klasik rota.",
  "country": "Türkiye",
  "city": "İstanbul",
  "duration_days": 3,
  "budget_level": 2,
  "currency_code": "TRY",
  "tags": ["culture", "food"],
  "months": [4, 5, 6, 9, 10],
  "tips": ["Müzelere sabah erken git, kuyruk kısa olur.", "Toplu taşımada İstanbulkart kullan."],
  "sort_rank": 10,
  "places": [
    { "key": "ayasofya", "name": "Ayasofya", "lat": 41.0086, "lon": 28.9802 },
    { "key": "sultanahmet", "name": "Sultanahmet Camii", "lat": 41.0054, "lon": 28.9768 },
    { "key": "yerebatan", "name": "Yerebatan Sarnıcı", "lat": 41.0084, "lon": 28.9779 },
    { "key": "topkapi", "name": "Topkapı Sarayı", "lat": 41.0115, "lon": 28.9834 },
    { "key": "kapalicarsi", "name": "Kapalıçarşı", "lat": 41.0107, "lon": 28.9681 },
    { "key": "galata", "name": "Galata Kulesi", "lat": 41.0256, "lon": 28.9741 },
    { "key": "ortakoy", "name": "Ortaköy", "lat": 41.0473, "lon": 29.0270 }
  ],
  "days": [
    { "day": 1, "activities": [
      { "start": "09:00", "end": "10:30", "title": "Ayasofya", "place": "ayasofya" },
      { "start": "10:45", "end": "11:30", "title": "Sultanahmet Camii", "place": "sultanahmet" },
      { "start": "11:45", "end": "12:30", "title": "Yerebatan Sarnıcı", "place": "yerebatan" },
      { "start": "14:00", "end": "17:00", "title": "Topkapı Sarayı", "place": "topkapi" }
    ] },
    { "day": 2, "activities": [
      { "start": "10:00", "end": "12:00", "title": "Kapalıçarşı", "place": "kapalicarsi" },
      { "start": "15:00", "end": "17:00", "title": "Boğaz turu", "note": "Eminönü iskelesinden." },
      { "start": "18:00", "end": "20:00", "title": "Ortaköy'de akşam", "place": "ortakoy" }
    ] },
    { "day": 3, "activities": [
      { "start": "10:00", "end": "11:00", "title": "Galata Kulesi", "place": "galata" },
      { "start": "11:30", "end": "14:00", "title": "İstiklal Caddesi ve Karaköy" }
    ] }
  ],
  "todos": [
    { "title": "Müze Kart al" },
    { "title": "İstanbulkart al" },
    { "title": "Rahat ayakkabı", "kind": "packing" },
    { "title": "Cami ziyareti için şal", "kind": "packing" }
  ]
}$json$::jsonb);

select private.explore_upsert($json${
  "slug": "kapadokya-2-gun",
  "title": "Kapadokya 2 gün",
  "summary": "Peri bacaları, vadiler ve yeraltı şehri.",
  "country": "Türkiye",
  "city": "Nevşehir",
  "duration_days": 2,
  "budget_level": 2,
  "currency_code": "TRY",
  "tags": ["nature", "culture", "romantic"],
  "months": [4, 5, 6, 9, 10],
  "tips": ["Balon turunu önceden ayırt; hava izin vermezse iptal olabilir."],
  "sort_rank": 20,
  "places": [
    { "key": "goreme", "name": "Göreme Açık Hava Müzesi", "lat": 38.6400, "lon": 34.8450 },
    { "key": "uchisar", "name": "Uçhisar Kalesi", "lat": 38.6306, "lon": 34.8050 },
    { "key": "pasabag", "name": "Paşabağ", "lat": 38.6720, "lon": 34.8540 },
    { "key": "derinkuyu", "name": "Derinkuyu Yeraltı Şehri", "lat": 38.3740, "lon": 34.7350 }
  ],
  "days": [
    { "day": 1, "activities": [
      { "start": "05:30", "end": "08:00", "title": "Balon turu", "note": "İsteğe bağlı." },
      { "start": "10:00", "end": "12:30", "title": "Göreme Açık Hava Müzesi", "place": "goreme" },
      { "start": "17:00", "end": "18:30", "title": "Uçhisar'da gün batımı", "place": "uchisar" }
    ] },
    { "day": 2, "activities": [
      { "start": "09:30", "end": "11:00", "title": "Paşabağ", "place": "pasabag" },
      { "start": "13:00", "end": "15:00", "title": "Derinkuyu Yeraltı Şehri", "place": "derinkuyu" }
    ] }
  ],
  "todos": [
    { "title": "Balon turunu ayırt" },
    { "title": "Kalın mont (sabahlar serin)", "kind": "packing" }
  ]
}$json$::jsonb);

select private.explore_upsert($json${
  "slug": "roma-4-gun",
  "title": "Roma 4 gün",
  "summary": "Antik Roma, Vatikan ve meydanlar.",
  "country": "İtalya",
  "city": "Roma",
  "duration_days": 4,
  "budget_level": 3,
  "currency_code": "EUR",
  "tags": ["culture", "food", "romantic"],
  "months": [3, 4, 5, 9, 10, 11],
  "tips": ["Kolezyum ve Vatikan Müzeleri için bileti internetten al."],
  "sort_rank": 30,
  "places": [
    { "key": "kolezyum", "name": "Kolezyum", "lat": 41.8902, "lon": 12.4922 },
    { "key": "forum", "name": "Roma Forumu", "lat": 41.8925, "lon": 12.4853 },
    { "key": "pantheon", "name": "Pantheon", "lat": 41.8986, "lon": 12.4769 },
    { "key": "trevi", "name": "Trevi Çeşmesi", "lat": 41.9009, "lon": 12.4833 },
    { "key": "vatikan", "name": "Vatikan Müzeleri", "lat": 41.9065, "lon": 12.4536 },
    { "key": "petrus", "name": "Aziz Petrus Bazilikası", "lat": 41.9022, "lon": 12.4539 }
  ],
  "days": [
    { "day": 1, "activities": [
      { "start": "09:00", "end": "11:30", "title": "Kolezyum", "place": "kolezyum" },
      { "start": "12:00", "end": "14:00", "title": "Roma Forumu", "place": "forum" }
    ] },
    { "day": 2, "activities": [
      { "start": "09:00", "end": "13:00", "title": "Vatikan Müzeleri", "place": "vatikan" },
      { "start": "14:00", "end": "15:30", "title": "Aziz Petrus Bazilikası", "place": "petrus" }
    ] },
    { "day": 3, "activities": [
      { "start": "10:00", "end": "11:00", "title": "Pantheon", "place": "pantheon" },
      { "start": "11:30", "end": "12:00", "title": "Trevi Çeşmesi", "place": "trevi" },
      { "start": "19:00", "end": "21:00", "title": "Trastevere'de akşam yemeği" }
    ] },
    { "day": 4, "activities": [
      { "start": "10:00", "end": "13:00", "title": "Serbest zaman ve alışveriş" }
    ] }
  ],
  "todos": [
    { "title": "Kolezyum biletini al" },
    { "title": "Vatikan Müzeleri biletini al" },
    { "title": "Priz adaptörü", "kind": "packing" }
  ]
}$json$::jsonb);
