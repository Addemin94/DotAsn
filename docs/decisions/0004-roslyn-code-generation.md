# ADR 0004: Generate C# as Roslyn syntax trees

**Status:** accepted

## Context
The first C# generator wrote classes, members and descriptor expressions as interpolated strings through an indenting
text writer. String templates are easy to start with, but nothing checks that the emitted text is well-formed C#:
missing commas, unbalanced brackets and unescaped literals only surface when the generated code is compiled, and
string escaping, keyword handling and layout are hand-written.

## Decision
`Asn1.CodeGen.CSharp` builds every declaration and expression with Roslyn's `SyntaxFactory`
(`Microsoft.CodeAnalysis.CSharp`): the compilation unit and file-scoped namespace, classes, enums, C# 14 extension
blocks and `field`-backed properties, lambdas, collection expressions, literals (string escaping by Roslyn), and XML
documentation comments built from Roslyn XML syntax nodes.

* `Cs` holds small construction helpers (`global::`-qualified names, literals, lambdas, object and collection
  creation, member declarations).
* `Literals` turns constraint trees, tags and runtime values into expressions.
* `SourceFormatter` produces the text: `NormalizeWhitespace`, then a `CSharpSyntaxRewriter` adds documentation
  comments (carried on members as syntax annotations), one blank line between members, and line breaks in long
  argument lists, collection expressions and `&&` chains. Output is deterministic with LF line endings.

## Consequences
* The generator cannot emit syntactically malformed C#; every node is typed.
* The generator depends on `Microsoft.CodeAnalysis.CSharp` (already used by the code generation tests).
* When the switch was made, the output for the asn1tools corpus files and the sample protocols was
  token-for-token identical to the string-based generator (ignoring white space, comments and trailing commas);
  only layout changed.
