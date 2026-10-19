# Diagnostics

Every diagnostic has a stable identifier. Ranges: `ASN0xxx` driver/internal, `ASN1xxx` syntax, `ASN2xxx` semantics, `ASN3xxx` constraints, `ASN4xxx` unsupported features, `ASN5xxx` lint style/maintainability, `ASN6xxx` encoding-rule interoperability, `ASN7xxx` code generation. This file is generated from `DiagnosticCatalog` by `DocumentationTests`.

| Id | Category | Default severity | Title | Message |
|---|---|---|---|---|
| ASN0001 | Internal | Error | Internal compiler error | Internal compiler error: {0} |
| ASN0002 | Internal | Error | Cannot read input | Cannot read '{0}': {1} |
| ASN0003 | Internal | Error | Resource limit exceeded | Resource limit exceeded: {0} |
| ASN0004 | Internal | Error | No input files | No ASN.1 input files were found{0} |
| ASN0005 | Internal | Error | Invalid configuration | Invalid configuration: {0} |
| ASN1001 | Syntax | Error | Unexpected character | Unexpected character '{0}' |
| ASN1002 | Syntax | Error | Unterminated string | Unterminated character string literal |
| ASN1003 | Syntax | Error | Unterminated comment | Unterminated /* */ comment |
| ASN1004 | Syntax | Error | Invalid bstring or hstring | Invalid bit or hexadecimal string literal: {0} |
| ASN1005 | Syntax | Error | Invalid identifier | Invalid identifier '{0}': {1} |
| ASN1006 | Syntax | Error | Invalid number | Invalid number literal '{0}' |
| ASN1010 | Syntax | Error | Syntax error | Expected {0} but found {1} |
| ASN1011 | Syntax | Error | Missing END | Module '{0}' is missing its END keyword |
| ASN1012 | Syntax | Error | No module definition | The input does not contain an ASN.1 module definition |
| ASN1013 | Syntax | Error | Invalid tag | Invalid tag: {0} |
| ASN1014 | Syntax | Warning | Deprecated notation | '{0}' is withdrawn X.208 notation; {1} |
| ASN1015 | Unsupported | Warning | Encoding control ignored | ENCODING-CONTROL section for '{0}' is not supported and was ignored |
| ASN1016 | Syntax | Error | Unexpected content | Unexpected content after module END: {0} |
| ASN1017 | Syntax | Error | Invalid real number | Invalid real number literal '{0}' |
| ASN1018 | Syntax | Error | Nesting too deep | Definition nesting exceeds the maximum depth of {0} |
| ASN2001 | Semantic | Error | Undefined reference | '{0}' is not defined{1} |
| ASN2002 | Semantic | Error | Duplicate definition | '{0}' is already defined in module '{1}' |
| ASN2003 | Semantic | Error | Duplicate module | Module '{0}' is defined more than once |
| ASN2004 | Semantic | Error | Imported module not found | Module '{0}' imported by '{1}' was not found among the inputs |
| ASN2005 | Semantic | Error | Imported symbol not found | '{0}' is not defined or not exported by module '{1}' |
| ASN2006 | Semantic | Error | Invalid export | Exported symbol '{0}' is not defined or imported in module '{1}' |
| ASN2007 | Semantic | Error | Ambiguous reference | '{0}' is ambiguous between modules {1}; use a module-qualified reference |
| ASN2008 | Semantic | Error | Illegal recursive definition | Illegal recursive definition: {0} |
| ASN2009 | Semantic | Error | Duplicate component | Duplicate identifier '{0}' in {1} |
| ASN2010 | Semantic | Error | Ambiguous tags | Tag {0} of '{1}' is not distinct from '{2}' in {3} |
| ASN2011 | Semantic | Error | Duplicate enumeration identifier | Duplicate enumeration identifier '{0}' |
| ASN2012 | Semantic | Error | Duplicate enumeration value | Enumeration value {0} of '{1}' is already used by '{2}' |
| ASN2013 | Semantic | Error | Duplicate named number | Duplicate named number or bit {0} |
| ASN2014 | Semantic | Error | Invalid value | Value is not valid for type {0}: {1} |
| ASN2016 | Semantic | Error | Wrong reference kind | '{0}' is a {1} but a {2} is required here |
| ASN2017 | Semantic | Error | Invalid COMPONENTS OF | COMPONENTS OF requires a {0} type but '{1}' is {2} |
| ASN2018 | Semantic | Error | Invalid selection type | Invalid selection type: {0} |
| ASN2019 | Semantic | Error | DEFAULT violates constraint | DEFAULT value of '{0}' does not satisfy the constraint of its type |
| ASN2020 | Semantic | Error | Invalid tag number | Tag number must be a non-negative integer: {0} |
| ASN2021 | Semantic | Error | Illegal IMPLICIT tag | IMPLICIT tagging cannot be applied to {0}; the tag is treated as EXPLICIT |
| ASN2022 | Semantic | Error | Parameter count mismatch | '{0}' expects {1} parameter(s) but {2} were supplied |
| ASN2023 | Semantic | Error | Missing actual parameters | Parameterized '{0}' must be referenced with an actual parameter list |
| ASN2024 | Semantic | Warning | Module identifier mismatch | Import of '{0}' specifies an object identifier that differs from the module definition |
| ASN2027 | Semantic | Error | Empty CHOICE | CHOICE type must have at least one alternative |
| ASN2028 | Semantic | Error | Unknown component | '{0}' is not a component of {1} |
| ASN2029 | Semantic | Error | Missing component | Value of {0} is missing mandatory component '{1}' |
| ASN2030 | Semantic | Error | Invalid information object | Invalid information object or class usage: {0} |
| ASN2031 | Semantic | Error | Cyclic value | Value '{0}' is defined in terms of itself |
| ASN2032 | Semantic | Error | Invalid object identifier | Invalid object identifier value: {0} |
| ASN2033 | Semantic | Error | Invalid extension | Invalid extension notation: {0} |
| ASN2034 | Semantic | Error | Invalid OPTIONAL/DEFAULT | {0} |
| ASN3001 | Constraint | Error | Constraint not applicable | {0} constraint cannot be applied to {1} |
| ASN3002 | Constraint | Error | Empty range | Range {0} is empty |
| ASN3003 | Constraint | Error | Unsatisfiable constraint | The constraint on {0} permits no values |
| ASN3004 | Constraint | Error | Invalid constraint value | Invalid value in constraint: {0} |
| ASN3005 | Constraint | Warning | Constraint outside parent | Constraint {0} lies (partly) outside the parent type's permitted values {1} |
| ASN3006 | Constraint | Error | Unknown component in constraint | '{0}' is not a component of {1} |
| ASN3007 | Constraint | Error | Invalid pattern | Invalid PATTERN: {0} |
| ASN3008 | Constraint | Error | Invalid presence constraint | {0} |
| ASN4001 | Unsupported | Warning | Unsupported feature | {0} is not supported yet{1} |
| ASN4004 | Unsupported | Info | Constraint not enforced | {0} constraint is recorded but not enforced at run time |
| ASN5001 | Style | Warning | Type naming convention | Type '{0}' does not follow the {1} naming convention |
| ASN5002 | Style | Warning | Identifier naming convention | Identifier '{0}' does not follow the {1} naming convention |
| ASN5003 | Style | Info | Unused definition | '{0}' is neither referenced nor exported |
| ASN5004 | Style | Warning | Unused import | Imported symbol '{0}' is never used |
| ASN5005 | Style | Info | Redundant alias | '{0}' is a plain alias of '{1}' without tags or constraints |
| ASN5006 | Style | Warning | Confusing names | '{0}' differs from '{1}' only by letter case or hyphens |
| ASN5007 | CodeGeneration | Warning | C# name collision | '{0}' and '{1}' map to the same C# name '{2}'; the generator will disambiguate |
| ASN5008 | Style | Info | Excessive nesting | '{0}' nests anonymous structured types {1} levels deep; consider named type assignments |
| ASN5009 | Interoperability | Info | Not extensible | {0} '{1}' has no extension marker; adding components later breaks PER/OER compatibility |
| ASN5010 | Interoperability | Warning | Suspicious extensibility | {0} |
| ASN5011 | Interoperability | Info | Unconstrained INTEGER | '{0}' is an unconstrained INTEGER; PER/OER encode it with a length prefix and implementations may limit its size |
| ASN5012 | Interoperability | Info | ENUMERATED index encoding | ENUMERATED '{0}' is encoded by index in PER; reordering items or crossing a power of two changes the wire format (consider an extension marker) |
| ASN5013 | Interoperability | Info | PER fragmentation | '{0}' has no SIZE upper bound below 64K; PER encodes values of 16K units or more with fragmentation (X.691 11.9.3.8) |
| ASN5014 | Style | Info | Redundant constraint | {0} |
| ASN5015 | CodeGeneration | Info | C# keyword | '{0}' is a C# keyword and will be renamed to '{1}' in generated code |
| ASN5017 | Style | Info | Default tagging | Module '{0}' has no tag default and therefore uses EXPLICIT TAGS |
| ASN5020 | Style | Info | Unused value | Value '{0}' is neither referenced nor exported |
| ASN6001 | Interoperability | Warning | Encoding rule incompatibility | '{0}' cannot be encoded with {1}: {2} |
| ASN7001 | CodeGeneration | Info | Identifier renamed | '{0}' is generated as '{1}' ({2}) |
| ASN7002 | CodeGeneration | Warning | Generation unsupported | '{0}' cannot be generated: {1} |
