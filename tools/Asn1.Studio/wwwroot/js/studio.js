// Small browser helpers for DotAsn Studio. Storage can be unavailable (private mode, blocked site data); every
// access is guarded so the app works without it.
window.asn1Studio = {
    load: function (key) {
        try { return window.localStorage.getItem("asn1studio." + key); } catch { return null; }
    },
    save: function (key, value) {
        try { window.localStorage.setItem("asn1studio." + key, value); } catch { /* ignored */ }
    },
    copy: async function (text) {
        try { await navigator.clipboard.writeText(text); return true; } catch { return false; }
    }
};
