// Both runtimes open the same real OPFS save directory. No polling or uploads.
export const SHARED_NAMESPACE = 'public-celeste-shared-v1';
export const SAVE_LOCK = 'public-celeste-shared-save-session-v1';
const LEGACY_MODES = ['vanilla', 'randomizer'];
const GAME_MODES = ['vanilla', 'spring-collab'];
const LOCK_MODES = ['vanilla', 'randomizer', 'spring-collab'];
let routedMode;

async function write(folder, name, bytes) {
  const file = await folder.getFileHandle(name, {create:true});
  const writer = await file.createWritable();
  try { await writer.write(bytes); await writer.close(); }
  catch (error) { await writer.abort().catch(() => {}); throw error; }
}

async function collect(folder, prefix = '') {
  const result = [];
  for await (const [name, handle] of folder.entries()) {
    if (handle.kind === 'directory') result.push(...await collect(handle, prefix + name + '/'));
    else result.push({path:prefix + name, file:await handle.getFile()});
  }
  return result;
}

async function destination(root, path) {
  const parts = path.split('/');
  let folder = root;
  for (const part of parts.slice(0,-1)) folder = await folder.getDirectoryHandle(part, {create:true});
  return {folder, name:parts.at(-1)};
}

export async function migrateSharedSaves(origin) {
  const shared = await origin.getDirectoryHandle(SHARED_NAMESPACE, {create:true});
  const saves = await shared.getDirectoryHandle('Saves', {create:true});
  let firstMigration = false;
  try { await shared.getFileHandle('.MigrationComplete-v1'); }
  catch (error) { if (error.name !== 'NotFoundError') throw error; firstMigration = true; }
  // Preserve the original folders once, but continue importing newer legacy
  // saves on later launches. This recovers progress left in a private mode
  // folder by an older build without letting an older copy replace the shared
  // save that is already in use.
  const snapshots = firstMigration ? await shared.getDirectoryHandle('MigrationBackups', {create:true}) : null;
  const candidates = new Map();
  // Finish and verify both backups before modifying the shared directory.
  for (const mode of LEGACY_MODES) {
    let previous;
    try {
      const root = await origin.getDirectoryHandle(`public-celeste-${mode}-v1`);
      previous = await (await root.getDirectoryHandle('Celeste')).getDirectoryHandle('Saves');
    } catch (error) { if (error.name === 'NotFoundError') continue; throw error; }
    const backup = snapshots ? await snapshots.getDirectoryHandle(mode, {create:true}) : null;
    for (const entry of await collect(previous)) {
      if (backup) {
        const bytes = await entry.file.arrayBuffer();
        const target = await destination(backup, entry.path);
        await write(target.folder, target.name, bytes);
        const verified = await (await target.folder.getFileHandle(target.name)).getFile();
        const copy = new Uint8Array(await verified.arrayBuffer()), original = new Uint8Array(bytes);
        if (copy.length !== original.length || copy.some((byte,index) => byte !== original[index])) throw new Error('Save backup verification failed. Your original saves were not changed.');
      }
      const older = candidates.get(entry.path);
      if (!older || entry.file.lastModified > older.file.lastModified) candidates.set(entry.path, entry);
    }
  }
  for (const entry of candidates.values()) {
    const target = await destination(saves, entry.path);
    let current;
    try { current = await (await target.folder.getFileHandle(target.name)).getFile(); }
    catch (error) { if (error.name !== 'NotFoundError') throw error; }
    if (!current || entry.file.lastModified > current.lastModified) {
      await write(target.folder, target.name, await entry.file.arrayBuffer());
    }
  }
  if (firstMigration) await write(shared, '.MigrationComplete-v1', 'Original mode saves preserved; newest conflicting file selected.');
  return saves;
}

async function mergeMissingFiles(source, target) {
  for await (const [name, handle] of source.entries()) {
    if (handle.kind === 'directory') {
      const child = await target.getDirectoryHandle(name, {create:true});
      await mergeMissingFiles(handle, child);
      continue;
    }
    let existing;
    try { existing = await target.getFileHandle(name); }
    catch (error) { if (error.name !== 'NotFoundError') throw error; }
    if (!existing) await write(target, name, await (await handle.getFile()).arrayBuffer());
  }
}

export async function installSaveRouting(mode) {
  if (!GAME_MODES.includes(mode)) throw new Error('Invalid game mode.');
  if (routedMode) {
    if (routedMode !== mode) throw new Error('Only one game mode can run in this page.');
    return;
  }
  const proto = FileSystemDirectoryHandle.prototype;
  const getDirectory = proto.getDirectoryHandle;
  const entries = proto.entries;
  const origin = await navigator.storage.getDirectory();
  const root = await getDirectory.call(origin, `public-celeste-${mode}-v1`, {create:true});
  const celeste = await getDirectory.call(root, 'Celeste', {create:true});
  // Keep a physical entry for the WASM filesystem's type checks. Its old files
  // are preserved, but all subsequent access and listings use the shared handle.
  await getDirectory.call(celeste, 'Saves', {create:true});
  // Spring Collab archives are extracted under the mode root's Mods/ folder.
  // Keep Everest pointed at that exact folder; an earlier build redirected
  // the game to a different empty OPFS directory, making it silently boot
  // plain Celeste despite successfully downloading every mod.
  const mods = mode === 'spring-collab' ? await getDirectory.call(root, 'Mods', {create:true}) : null;
  if (mods) {
    // Keep a physical entry for the WASM filesystem's type checks. Migrate any
    // files a previous build exposed through its separate folder, but leave
    // that original folder intact and never overwrite installed package files.
    await getDirectory.call(celeste, 'Mods', {create:true});
    let previousMods;
    try { previousMods = await getDirectory.call(origin, 'public-celeste-spring-collab-mods-v1'); }
    catch (error) { if (error.name !== 'NotFoundError') throw error; }
    if (previousMods && !await previousMods.isSameEntry(mods)) await mergeMissingFiles(previousMods, mods);
  }
  const shared = await getDirectory.call(origin, SHARED_NAMESPACE, {create:true});
  const saves = await getDirectory.call(shared, 'Saves', {create:true});
  proto.getDirectoryHandle = async function(name, options) {
    if (name === 'Saves' && this.name === 'Celeste' && await this.isSameEntry(celeste)) return saves;
    if (name === 'Mods' && mods && (await this.isSameEntry(root) || await this.isSameEntry(celeste))) return mods;
    return getDirectory.call(this, name, options);
  };
  proto.entries = async function*() {
    const inCeleste = this.name === 'Celeste' && await this.isSameEntry(celeste);
    const inRoot = mods && await this.isSameEntry(root);
    for await (const [name, handle] of entries.call(this)) {
      yield [name, inCeleste && name === 'Saves' ? saves : mods && (inCeleste || inRoot) && name === 'Mods' ? mods : handle];
    }
  };
  proto[Symbol.asyncIterator] = proto.entries;
  proto.values = async function*() { for await (const [,handle] of this.entries()) yield handle; };
  proto.keys = async function*() { for await (const [name] of this.entries()) yield name; };
  routedMode = mode;
}

export async function withSaveSession(locks, action) {
  if (!locks) throw new Error('Shared saves require a current Chrome or Edge browser with Web Locks support.');
  const names = [SAVE_LOCK, ...LOCK_MODES.map(mode => `public-celeste-${mode}-v1`)];
  const acquire = index => index === names.length ? action() : locks.request(names[index], {ifAvailable:true}, lock => {
    if (!lock) throw new Error('A Celeste mode is already open in another tab. Close it before switching modes so both games use the latest save safely.');
    return acquire(index + 1);
  });
  return acquire(0);
}
