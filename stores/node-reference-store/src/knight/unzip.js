/**
 * Reading a KNIGHT artifact, which is a zip.
 *
 * Written out rather than taken from npm, and that is the point of this store
 * rather than laziness. A reference implementation exists to show what a team
 * has to build, and "add this dependency" shows nothing — a store integrating
 * KNIGHT in Go or Elixir cannot install a node package, and what they need to
 * see is that the format is an ordinary zip with nothing bespoke in it.
 *
 * Deliberately narrow. It reads the central directory, supports stored and
 * deflated entries, and refuses everything else rather than guessing: an
 * artifact this cannot read is an artifact that should fail loudly at install,
 * not one that should be half-unpacked.
 */

import { inflateRawSync } from 'node:zlib';

const END_OF_CENTRAL_DIRECTORY = 0x06054b50;
const CENTRAL_FILE_HEADER = 0x02014b50;

const STORED = 0;
const DEFLATED = 8;

export class ArtifactUnreadable extends Error {
  constructor(message) {
    super(message);
    this.name = 'ArtifactUnreadable';
  }
}

/**
 * Every file in the archive, as `{name, bytes}`.
 *
 * Paths are checked here rather than by the caller, because this is the last
 * place that knows both the archive and the fact that its contents are about to
 * be written to disk. An entry naming `../../etc/anything` is a delivered
 * artifact reaching out of the directory it was given, and the only safe
 * response is to refuse the whole archive — a partially unpacked one is worse
 * than none.
 */
export function readArchive(buffer) {
  const end = findEndOfCentralDirectory(buffer);
  const count = buffer.readUInt16LE(end + 10);
  let offset = buffer.readUInt32LE(end + 16);

  const entries = [];

  for (let index = 0; index < count; index += 1) {
    if (buffer.readUInt32LE(offset) !== CENTRAL_FILE_HEADER) {
      throw new ArtifactUnreadable(`Entry ${index} is not where the central directory says it is.`);
    }

    const method = buffer.readUInt16LE(offset + 10);
    const compressedSize = buffer.readUInt32LE(offset + 20);
    const nameLength = buffer.readUInt16LE(offset + 28);
    const extraLength = buffer.readUInt16LE(offset + 30);
    const commentLength = buffer.readUInt16LE(offset + 32);
    const localOffset = buffer.readUInt32LE(offset + 42);
    const name = buffer.toString('utf8', offset + 46, offset + 46 + nameLength);

    offset += 46 + nameLength + extraLength + commentLength;

    // A directory entry. Nothing to write: the tree is created from the paths of
    // the files themselves.
    if (name.endsWith('/')) {
      continue;
    }

    assertSafePath(name);
    entries.push({ name, bytes: readLocalEntry(buffer, localOffset, method, compressedSize) });
  }

  return entries;
}

function readLocalEntry(buffer, offset, method, compressedSize) {
  const nameLength = buffer.readUInt16LE(offset + 26);
  const extraLength = buffer.readUInt16LE(offset + 28);
  const start = offset + 30 + nameLength + extraLength;
  const raw = buffer.subarray(start, start + compressedSize);

  if (method === STORED) {
    return Buffer.from(raw);
  }

  if (method === DEFLATED) {
    return inflateRawSync(raw);
  }

  throw new ArtifactUnreadable(`Compression method ${method} is not one this store can read.`);
}

/**
 * The end-of-central-directory record, which is at the end unless the archive
 * has a comment — so it is searched for backwards over the 64KB a comment may
 * occupy, which is what every zip reader does.
 */
function findEndOfCentralDirectory(buffer) {
  const earliest = Math.max(0, buffer.length - 0xffff - 22);

  for (let offset = buffer.length - 22; offset >= earliest; offset -= 1) {
    if (buffer.readUInt32LE(offset) === END_OF_CENTRAL_DIRECTORY) {
      return offset;
    }
  }

  throw new ArtifactUnreadable('This is not a zip archive: no end-of-central-directory record.');
}

function assertSafePath(name) {
  const normalised = name.replace(/\\/g, '/');

  if (
    normalised.startsWith('/') ||
    normalised.split('/').includes('..') ||
    /^[a-zA-Z]:/.test(normalised)
  ) {
    throw new ArtifactUnreadable(`The archive contains '${name}', which points outside the package.`);
  }
}
