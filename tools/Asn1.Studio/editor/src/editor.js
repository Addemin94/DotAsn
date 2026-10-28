// The code editor of DotAsn Studio: CodeMirror 6 with inline diagnostics, ASN.1 highlighting from the DotAsn lexer
// (tokens computed in .NET by SyntaxClassifier and sent after each edit) and JSON highlighting.
// Used from Components/CodeEditor.razor through JS interop; colours come from the Studio's CSS variables (app.css).

import { closeBrackets, closeBracketsKeymap } from '@codemirror/autocomplete';
import { defaultKeymap, history, historyKeymap, indentWithTab } from '@codemirror/commands';
import { json } from '@codemirror/lang-json';
import { bracketMatching, HighlightStyle, indentOnInput, syntaxHighlighting } from '@codemirror/language';
import { lintGutter, lintKeymap, setDiagnostics } from '@codemirror/lint';
import { highlightSelectionMatches, searchKeymap } from '@codemirror/search';
import { EditorSelection, EditorState, RangeSetBuilder, StateEffect, StateField } from '@codemirror/state';
import {
  Decoration, drawSelection, EditorView, highlightActiveLine, highlightActiveLineGutter, keymap, lineNumbers, placeholder,
} from '@codemirror/view';
import { tags as t } from '@lezer/highlight';

// JSON highlighting (@codemirror/lang-json); ASN.1 is highlighted by the DotAsn token classes in app.css.
const highlight = HighlightStyle.define([
  { tag: t.propertyName, color: 'var(--syn-field)' },
  { tag: t.string, color: 'var(--syn-string)' },
  { tag: [t.number, t.bool, t.null], color: 'var(--syn-number)' },
  { tag: [t.bracket, t.punctuation, t.separator], color: 'var(--syn-punctuation)' },
]);

const theme = EditorView.theme({
  '&': {
    backgroundColor: 'var(--code)',
    color: 'var(--text)',
    border: '1px solid var(--border)',
    borderRadius: '6px',
    fontSize: '13px',
  },
  '&.cm-focused': { outline: '2px solid var(--accent)', outlineOffset: '-1px' },
  '.cm-scroller': {
    fontFamily: 'ui-monospace, "Cascadia Code", "JetBrains Mono", Consolas, monospace',
    lineHeight: '1.5',
    minHeight: 'calc(var(--editor-rows, 12) * 1.5em)',
    maxHeight: '75vh',
  },
  '.cm-content': { caretColor: 'var(--text)' },
  '.cm-cursor, .cm-dropCursor': { borderLeftColor: 'var(--text)' },
  '&.cm-focused > .cm-scroller > .cm-selectionLayer .cm-selectionBackground, .cm-selectionBackground, .cm-content ::selection':
    { backgroundColor: 'var(--editor-selection)' },
  '.cm-activeLine': { backgroundColor: 'var(--editor-active-line)' },
  '.cm-gutters': { backgroundColor: 'var(--panel)', color: 'var(--muted)', borderRight: '1px solid var(--border)' },
  '.cm-activeLineGutter': { backgroundColor: 'var(--editor-active-line)', color: 'var(--text)' },
  '.cm-matchingBracket': { backgroundColor: 'var(--editor-selection)', outline: '1px solid var(--border)' },
  '.cm-placeholder': { color: 'var(--muted)' },
  '.cm-tooltip': { backgroundColor: 'var(--panel)', color: 'var(--text)', border: '1px solid var(--border)' },
  '.cm-tooltip-lint': { maxWidth: '40rem' },
});

// Highlighting by tokens from .NET: (start, length, class) triples, where class indexes the class names given at
// creation (SyntaxClass). The decorations move with edits until the tokens of the new text arrive.
const setTokens = StateEffect.define();
const tokenField = StateField.define({
  create: () => Decoration.none,
  update(decorations, tr) {
    decorations = decorations.map(tr.changes);
    for (const effect of tr.effects) {
      if (effect.is(setTokens)) {
        decorations = effect.value;
      }
    }

    return decorations;
  },
  provide: field => EditorView.decorations.from(field),
});

const languages = { json, text: () => [] };

/** One editor instance, controlled from .NET. */
class Editor {
  constructor(host, dotnet, options) {
    this.dotnet = dotnet;
    this.external = false;
    this.version = 0;
    // One mark per token class, e.g. class "dotasn-typereference" for SyntaxClass.TypeReference.
    this.marks = (options.tokenClasses ?? []).map(name => Decoration.mark({ class: `dotasn-${name.toLowerCase()}` }));
    host.style.setProperty('--editor-rows', String(options.rows ?? 12));
    this.view = new EditorView({
      parent: host,
      state: EditorState.create({
        doc: options.value ?? '',
        extensions: [
          lineNumbers(),
          highlightActiveLineGutter(),
          lintGutter(),
          history(),
          drawSelection(),
          indentOnInput(),
          bracketMatching(),
          closeBrackets(),
          highlightActiveLine(),
          highlightSelectionMatches(),
          syntaxHighlighting(highlight),
          theme,
          options.tokenClasses ? tokenField : (languages[options.language] ?? languages.text)(),
          options.placeholder ? placeholder(options.placeholder) : [],
          options.readOnly ? [EditorState.readOnly.of(true)] : [],
          EditorView.contentAttributes.of({ 'aria-label': options.label ?? 'Code editor', spellcheck: 'false' }),
          keymap.of([...closeBracketsKeymap, ...defaultKeymap, ...searchKeymap, ...historyKeymap, ...lintKeymap, indentWithTab]),
          EditorView.updateListener.of(update => {
            if (update.docChanged) {
              this.version++;
              if (!this.external) {
                this.changed(update.state.doc.toString());
              }
            }
          }),
        ],
      }),
    });
    if (options.tokens) {
      this.setTokens(options.tokens);
    }
  }

  /** Reports an edit; .NET answers with the tokens of the new text, applied unless the text changed meanwhile. */
  async changed(text) {
    const version = this.version;
    const tokens = await this.dotnet.invokeMethodAsync('OnChange', text);
    if (tokens && version === this.version) {
      this.setTokens(tokens);
    }
  }

  /** Highlights the text with (start, length, class) triples, ordered by start. */
  setTokens(tokens) {
    const length = this.view.state.doc.length;
    const builder = new RangeSetBuilder();
    for (let i = 0; i + 2 < tokens.length; i += 3) {
      const from = tokens[i];
      const to = Math.min(from + tokens[i + 1], length);
      const mark = this.marks[tokens[i + 2]];
      if (mark && from < to) {
        builder.add(from, to, mark);
      }
    }

    this.view.dispatch({ effects: setTokens.of(builder.finish()) });
  }

  /** Replaces the text (without notifying .NET, which set it). */
  setValue(text) {
    if (text === this.view.state.doc.toString()) {
      return;
    }

    this.external = true;
    try {
      this.view.dispatch({ changes: { from: 0, to: this.view.state.doc.length, insert: text } });
    } finally {
      this.external = false;
    }
  }

  /** Shows diagnostics: [{ from, to, severity: 'error' | 'warning' | 'info', message, source }], as UTF-16 offsets. */
  setDiagnostics(diagnostics) {
    const length = this.view.state.doc.length;
    const list = diagnostics.map(d => {
      const from = Math.min(Math.max(0, d.from), length);
      // A diagnostic without extent marks the character at its position (or the end of the text).
      const to = Math.min(Math.max(from + (d.to > d.from ? d.to - d.from : 1), from), length);
      return { from, to, severity: d.severity, message: d.message, source: d.source };
    });
    this.view.dispatch(setDiagnostics(this.view.state, list));
  }

  /** Moves the cursor to a one-based line and column, scrolls it into view and focuses the editor. */
  goTo(line, column) {
    const doc = this.view.state.doc;
    const target = doc.line(Math.min(Math.max(1, line), doc.lines));
    const pos = Math.min(target.from + Math.max(0, column - 1), target.to);
    this.view.dispatch({ selection: EditorSelection.cursor(pos), scrollIntoView: true });
    this.view.focus();
  }

  dispose() {
    this.view.destroy();
  }
}

/** Creates an editor in `host`; `dotnet` receives OnChange(text) after each edit. */
export function create(host, dotnet, options) {
  return new Editor(host, dotnet, options ?? {});
}
