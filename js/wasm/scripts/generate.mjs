// Generates TypeScript for every test-vector case set (test-vectors/cases/*.json) and every test specification
// (test/specs/*.asn) into test/generated/<name>/ with the asn1 CLI. Set ASN1_CLI to an asn1 executable to skip `dotnet run`.
import { execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, readdirSync, rmSync, statSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const root = resolve(here, '../../..');
const out = resolve(here, '../test/generated');
// The most recently built CLI (Debug or Release).
const built = ['Release', 'Debug'].map(c => join(root, `src/Asn1.Cli/bin/${c}/net10.0/asn1`)).filter(existsSync)
  .sort((a, b) => statSync(b).mtimeMs - statSync(a).mtimeMs)[0];
const cli = process.env.ASN1_CLI ? [process.env.ASN1_CLI] : built ? [built] : ['dotnet', 'run', '--project', join(root, 'src/Asn1.Cli'), '--'];

rmSync(out, { recursive: true, force: true });
const sets = readdirSync(join(root, 'test-vectors/cases')).filter(f => f.endsWith('.json')).sort().map(file => ({
  name: file.replace(/\.json$/, ''),
  specs: JSON.parse(readFileSync(join(root, 'test-vectors/cases', file), 'utf8')).specs.map(s => join(root, s)),
}));
const specDir = resolve(here, '../test/specs');
for (const file of readdirSync(specDir).filter(f => f.endsWith('.asn')).sort()) {
  sets.push({ name: file.replace(/\.asn$/, ''), specs: [join(specDir, file)] });
}

for (const { name, specs } of sets) {
  const dir = join(out, name);
  mkdirSync(dir, { recursive: true });
  const [cmd, ...args] = cli;
  execFileSync(cmd, [...args, 'generate-typescript', ...specs, '-o', dir, '--import-extension', '.ts', '--quiet'], { stdio: 'inherit' });
}

console.log(`generated TypeScript for ${sets.length} set(s) in ${out}`);
