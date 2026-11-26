import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

export function assertEmailLogPrivacy(log, markers) {
  if (typeof log !== 'string' || !Array.isArray(markers) || markers.length < 1 || markers.length > 32
    || markers.some(value => typeof value !== 'string' || value.length < 1 || value.length > 16_384)) {
    throw new Error('Private email marker evidence is invalid.');
  }
  const unicodeDecoded = log.replace(/\\u([0-9a-f]{4})/gi, (_, value) => String.fromCharCode(Number.parseInt(value, 16)));
  for (const marker of markers) {
    const forms = [marker, JSON.stringify(marker).slice(1, -1), encodeURIComponent(marker)];
    if (forms.some(value => log.includes(value) || unicodeDecoded.includes(value))) {
      throw new Error('Private email content was found in API diagnostics.');
    }
  }
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    if (process.argv.length !== 4) throw new Error('Expected private state and API log paths.');
    const state = JSON.parse(readFileSync(process.argv[2], 'utf8'));
    assertEmailLogPrivacy(readFileSync(process.argv[3], 'utf8'), state.privateMarkers);
    console.log('Email diagnostics privacy check passed.');
  } catch {
    // Paths, malformed fixture content and marker values never become diagnostics.
    console.error('Email diagnostics privacy qualification failed.');
    process.exitCode = 1;
  }
}
