// Static-only installer. Neither package contains save files or user settings.
import {migrateSharedSaves, installSaveRouting, withSaveSession} from './shared-saves.mjs';
export const mode = new URLSearchParams(location.search).get('mode') === 'spring-collab' ? 'spring-collab' : 'vanilla';
export const namespace = `public-celeste-${mode}-v1`;
const status = document.querySelector('#status');
const progress = document.querySelector('#progress');
const title = document.querySelector('#title');
const retry = document.querySelector('#retry');
retry.addEventListener('click', () => location.reload());

function fail(error) {
  title.textContent = 'Could not start the game';
  status.textContent = error?.message || String(error);
  status.classList.add('error');
  retry.style.display = 'inline-block';
  console.error(error);
}

export class ByteReader {
  constructor(stream) { this.reader = stream.getReader(); this.pending = new Uint8Array(); }
  async piece(limit) {
    if (!this.pending.length) {
      const {value, done} = await this.reader.read();
      if (done) throw new Error('The game download ended unexpectedly. Retry the download.');
      this.pending = value;
    }
    const part = this.pending.subarray(0, limit);
    this.pending = this.pending.subarray(part.length);
    return part;
  }
  async exact(size) {
    const result = new Uint8Array(size);
    let offset = 0;
    while (offset < size) {
      const part = await this.piece(size - offset);
      result.set(part, offset); offset += part.length;
    }
    return result;
  }
}

export async function extractTar(stream, root, fileCallback = () => {}) {
  const reader = new ByteReader(stream);
  const decoder = new TextDecoder();
  const field = bytes => decoder.decode(bytes).replace(/\0.*$/, '').trim();
  for (;;) {
    const header = await reader.exact(512);
    if (header.every(byte => byte === 0)) break;
    const prefix = field(header.subarray(345, 500));
    const name = (prefix ? prefix + '/' : '') + field(header.subarray(0, 100));
    const parts = name.split('/');
    if (!name || parts.some(part => !part || part === '.' || part === '..' || part.includes('\\')) || parts.some(part => ['saves', 'cache', 'backups'].includes(part.toLowerCase())) || /\.(celeste|pdb|log|sum)$/i.test(name)) throw new Error('Unsafe or personal file detected in game package.');
    let checksum = 0;
    for (let i = 0; i < 512; i++) checksum += i >= 148 && i < 156 ? 32 : header[i];
    if (checksum !== parseInt(field(header.subarray(148, 156)), 8)) throw new Error('Invalid game package header.');
    const size = parseInt(field(header.subarray(124, 136)) || '0', 8);
    if (!Number.isSafeInteger(size) || size < 0 || ![0, 48].includes(header[156])) throw new Error('Unsupported game package entry.');
    let folder = root;
    for (const part of parts.slice(0, -1)) folder = await folder.getDirectoryHandle(part, {create:true});
    const file = await folder.getFileHandle(parts.at(-1), {create:true});
    const writer = await file.createWritable();
    try {
      let left = size;
      while (left) { const data = await reader.piece(left); await writer.write(data); left -= data.length; }
      await writer.close();
    } catch (error) { await writer.abort().catch(() => {}); throw error; }
    if (size % 512) await reader.exact(512 - size % 512);
    fileCallback(name);
  }
  // Read the remaining end blocks to validate the gzip trailer/checksum too.
  while (!(await reader.reader.read()).done) {}
}

function packageStream(pkg, update) {
  let chunkIndex = 0, current = null, offset = 0, loaded = 0;
  return new ReadableStream({
    async pull(controller) {
      try {
        if (!current || offset >= current.length) {
          if (chunkIndex >= pkg.chunks.length) { controller.close(); return; }
          const chunk = pkg.chunks[chunkIndex++];
          const response = await fetch(new URL(chunk.url, location.href));
          if (!response.ok) throw new Error(`Game download failed (${response.status}). Please try again.`);
          current = new Uint8Array(await response.arrayBuffer()); offset = 0;
          if (current.length !== chunk.size) throw new Error('Incomplete game download. Please try again.');
          const digest = await crypto.subtle.digest('SHA-256', current);
          const hash = [...new Uint8Array(digest)].map(byte => byte.toString(16).padStart(2,'0')).join('');
          if (hash !== chunk.sha256) throw new Error('The game download failed its integrity check. Please try again.');
          loaded += current.length; update(loaded / pkg.bytes * 100);
        }
        const end = Math.min(offset + 65536, current.length);
        controller.enqueue(current.subarray(offset, end)); offset = end;
      } catch (error) { controller.error(error); }
    }
  }).pipeThrough(new DecompressionStream('gzip'));
}

async function writeText(folder, name, text) {
  const file = await folder.getFileHandle(name, {create:true});
  const writer = await file.createWritable(); await writer.write(text); await writer.close();
}

async function install(root, pkg, label, weight, before) {
  const marker = `.PublicPackage-${pkg.id}`;
  try { await root.getFileHandle(marker); return; } catch (error) { if (error.name !== 'NotFoundError') throw error; }
  status.textContent = `${label}: downloading and installing…`;
  let last = 0;
  const stream = packageStream(pkg, percent => { progress.value = before + percent * weight; });
  await extractTar(stream, root, name => {
    if (performance.now() - last > 250) { status.textContent = `${label}: installing ${name}`; last = performance.now(); }
  });
  await writeText(root, marker, pkg.id);
}

async function start() {
  if (!isSecureContext || !navigator.storage?.getDirectory || !globalThis.DecompressionStream) throw new Error('Please open the HTTPS website in an up-to-date desktop Chrome or Edge browser, outside Incognito.');
  if (!crossOriginIsolated) {
    status.textContent = 'Enabling the browser features needed by the game. This page may reload once…';
    setTimeout(() => { if (!crossOriginIsolated) fail(new Error('The game needs browser service workers and cross-origin isolation. Site restrictions may be blocking them. Try current desktop Chrome with site data enabled.')); }, 15000);
    return;
  }
  const root = await (await navigator.storage.getDirectory()).getDirectoryHandle(namespace, {create:true});
  const response = await fetch('./package-manifest.json?mhh-hook-isolation-v2');
  if (!response.ok) throw new Error('Could not download the game manifest.');
  const manifest = await response.json();
  title.textContent = mode === 'spring-collab' ? 'Preparing Spring Collab 2020' : 'Preparing Celeste';
  await navigator.storage.persist?.().catch(() => false);
  const hasCollab = mode === 'spring-collab';
  await install(root, manifest.base, 'Celeste', hasCollab ? 0.8 : 1, 0);
  if (hasCollab) {
    await install(root, manifest.springCollab, 'Spring Collab 2020 and required files', 0.2, 80);
    // Apply small compatibility overlays separately so existing installs do
    // not have to download the entire Spring Collab archive again.
    if (manifest.maxHelpingHandPatch) {
      await install(root, manifest.maxHelpingHandPatch, 'Spring Collab compatibility update', 0, 100);
    }
  }
  status.textContent = 'Preserving existing progress and preparing shared saves…';
  await migrateSharedSaves(await navigator.storage.getDirectory());
  await installSaveRouting(mode);
  await writeText(root, '.PublicGameReady', 'clean-v1');
  progress.value = 100;
  status.textContent = 'Starting the game…';
  // The published bundle and native worker both mount this mode's private folder.
  await import('./assets/index-9lMdcnsS.js?shared-saves=2');
  document.querySelector('#startup').remove();
  // Stop public.css's page layout from interfering with the game's canvas.
  document.querySelector('link[href="./public.css"]').remove();
}

withSaveSession(navigator.locks, async () => {
    await start();
    await new Promise(() => {});
}).catch(fail);
