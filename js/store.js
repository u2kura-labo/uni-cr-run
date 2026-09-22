// ブラウザの中だけに試合データを保存する（IndexedDB）。サーバーには何も送らない。
// 保存するのは JSONL の1行をそのまま読んだもの（raw）。表示用の変換は読み出すたびに行うので、
// 変換の仕方を後で変えても、保存し直す必要はない。
const DB_NAME = 'uni-cr-run';
const DB_VERSION = 1;
const STORE = 'matches';

let dbPromise = null;

function openDb() {
  if (!dbPromise) {
    dbPromise = new Promise((resolve, reject) => {
      const req = indexedDB.open(DB_NAME, DB_VERSION);
      req.onupgradeneeded = () => {
        req.result.createObjectStore(STORE, { keyPath: 'id' });
      };
      req.onsuccess = () => resolve(req.result);
      req.onerror = () => reject(req.error);
    });
  }
  return dbPromise;
}

function done(tx) {
  return new Promise((resolve, reject) => {
    tx.oncomplete = () => resolve();
    tx.onerror = () => reject(tx.error);
    tx.onabort = () => reject(tx.error);
  });
}

function request(req) {
  return new Promise((resolve, reject) => {
    req.onsuccess = () => resolve(req.result);
    req.onerror = () => reject(req.error);
  });
}

export async function loadAll() {
  const db = await openDb();
  const tx = db.transaction(STORE, 'readonly');
  return request(tx.objectStore(STORE).getAll());
}

// まだ保存していない id だけを追加する。返り値は { added, duplicates }。
export async function addRecords(records) {
  const db = await openDb();
  const tx = db.transaction(STORE, 'readwrite');
  const store = tx.objectStore(STORE);
  const existing = new Set(await request(store.getAllKeys()));
  let added = 0;
  for (const rec of records) {
    if (existing.has(rec.id)) continue;
    store.put({ id: rec.id, raw: rec.raw, importedAt: Date.now() });
    existing.add(rec.id);
    added++;
  }
  await done(tx);
  return { added, duplicates: records.length - added };
}

export async function clearAll() {
  const db = await openDb();
  const tx = db.transaction(STORE, 'readwrite');
  tx.objectStore(STORE).clear();
  await done(tx);
}
