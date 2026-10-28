// The asn1tools vectors through the WebAssembly runtime, loaded with the schema generated next to the TypeScript
// (dotasn-schema.ts). Run `npm run generate` first.

import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { defineVectorSuites, generatedDir, type RuleSet } from './vector-harness.ts';
import { loadDotAsn } from '@dotasn/wasm';

const sessions = new Map<string, Promise<RuleSet>>();

async function rulesFor(caseSet: string): Promise<RuleSet> {
  const { schema } = (await import(pathToFileURL(join(generatedDir(caseSet), 'dotasn-schema.ts')).href)) as { schema: string };
  const asn1 = await loadDotAsn(schema);
  return { ber: asn1.ber, der: asn1.der, uper: asn1.uper, aper: asn1.aper, oer: asn1.oer, coer: asn1.coer, jer: asn1.jer };
}

defineVectorSuites(caseSet => {
  let rules = sessions.get(caseSet);
  if (rules === undefined) {
    rules = rulesFor(caseSet);
    sessions.set(caseSet, rules);
  }

  return rules;
});
