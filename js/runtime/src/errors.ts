// The base class of DotAsn errors (the encoding errors of @dotasn/wasm extend it).

/** Base class of all DotAsn errors. */
export class Asn1Error extends Error {
  /** The component path where the problem was found, e.g. "children[1].name". */
  readonly path: string;

  constructor(message: string, path: string) {
    super(path ? `${message} in ${path}` : message);
    this.name = new.target.name;
    this.path = path;
  }
}
