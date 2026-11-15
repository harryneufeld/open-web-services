// Immediate feedback when the user opens another page: the clicked link is marked as pending and a thin bar runs
// along the top until Blazor's enhanced navigation has swapped in the new page (which then shows its own skeleton).
(() => {
    const root = document.documentElement;
    const delayMs = 80; // fast page changes stay flicker-free
    const giveUpMs = 15000; // never leave the bar running if a navigation fails silently
    let showTimer = 0;
    let giveUpTimer = 0;

    const done = () => {
        clearTimeout(showTimer);
        clearTimeout(giveUpTimer);
        root.classList.remove("is-navigating");
        document.querySelectorAll("a.is-pending").forEach(a => a.classList.remove("is-pending"));
    };

    const start = (link) => {
        done();
        link?.classList.add("is-pending");
        showTimer = setTimeout(() => root.classList.add("is-navigating"), delayMs);
        giveUpTimer = setTimeout(done, giveUpMs);
    };

    document.addEventListener("click", (e) => {
        if (e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) return;
        const link = e.target instanceof Element ? e.target.closest("a[href]") : null;
        if (!link || (link.target && link.target !== "_self") || link.hasAttribute("download")) return;
        const url = new URL(link.href, location.href);
        if (url.origin !== location.origin) return;
        if (url.pathname === location.pathname && url.search === location.search) return; // same page or an anchor
        start(link);
    }, true);

    window.addEventListener("popstate", () => start(null));
    window.addEventListener("pageshow", done);
    window.Blazor?.addEventListener("enhancedload", done);
})();
