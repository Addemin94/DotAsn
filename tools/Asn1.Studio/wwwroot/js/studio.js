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
    },
    download: function (name, text, type) {
        var url = URL.createObjectURL(new Blob([text], { type: type || "text/plain" }));
        var a = document.createElement("a");
        a.href = url;
        a.download = name;
        document.body.appendChild(a);
        a.click();
        a.remove();
        setTimeout(function () { URL.revokeObjectURL(url); }, 1000);
    }
};
