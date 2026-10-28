// Bundles the editor (src/editor.js with CodeMirror) into ../wwwroot/js/editor.js, an ES module the Studio imports.
// The output is committed so the Studio builds without Node; CI rebuilds it and checks that it is unchanged.
import { build } from 'esbuild';
import { readdirSync, readFileSync, writeFileSync } from 'node:fs';

await build({
  entryPoints: ['src/editor.js'],
  bundle: true,
  format: 'esm',
  minify: true,
  target: 'es2022',
  legalComments: 'none',
  banner: {
    js: '/* DotAsn Studio editor (tools/Asn1.Studio/editor). Bundles CodeMirror 6 and Lezer, MIT License: see editor.LICENSE.txt. */',
  },
  outfile: '../wwwroot/js/editor.js',
  logLevel: 'info',
});

// The MIT notices of the bundled packages (CodeMirror, Lezer and their dependencies).
const packages = ['@codemirror', '@lezer', '@marijn']
  .flatMap(scope => readdirSync(`node_modules/${scope}`).map(name => `${scope}/${name}`))
  .concat(['crelt', 'style-mod', 'w3c-keyname'])
  .sort();
const notices = packages.map(name => {
  const { version } = JSON.parse(readFileSync(`node_modules/${name}/package.json`, 'utf8'));
  return `${name} ${version}\n\n${readFileSync(`node_modules/${name}/LICENSE`, 'utf8').trim()}\n`;
});
writeFileSync('../wwwroot/js/editor.LICENSE.txt', `Third-party software bundled in editor.js\n\n${notices.join('\n---\n\n')}`);
