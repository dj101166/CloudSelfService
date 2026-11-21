// Browser-side state this app owns: which theme is showing, and whether the sidebar is
// collapsed. Both are also applied by an inline script in App.razor before first paint —
// this file is what changes them afterwards, and the inline script is the authority on
// what is showing when the circuit connects.
//
// Three modes (light / dark / system), written as data-bs-theme.
window.cssp = {
    THEME_KEY: 'cloudSelfServiceTheme',
    SIDEBAR_KEY: 'cloudSelfServiceSidebarCollapsed',

    // Resolves a stored preference to the theme to actually apply. 'system' follows the
    // OS; anything else is taken at face value, which keeps a previously stored
    // 'light'/'dark' working untouched.
    resolve: function (mode) {
        if (mode === 'system') {
            return window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches
                ? 'dark' : 'light';
        }
        return mode === 'dark' ? 'dark' : 'light';
    },

    setTheme: function (mode) {
        try { localStorage.setItem(this.THEME_KEY, mode); } catch (e) { /* private mode */ }
        document.documentElement.setAttribute('data-bs-theme', this.resolve(mode));
    },

    // The theme currently showing, read from the DOM rather than from storage: the
    // inline script has already decided, and it is the authority.
    getTheme: function () {
        return document.documentElement.getAttribute('data-bs-theme') || 'light';
    },

    // The stored preference, which is not the same thing — 'system' is a valid answer
    // here and never a valid answer from getTheme.
    getMode: function () {
        try { return localStorage.getItem(this.THEME_KEY) || 'system'; } catch (e) { return 'system'; }
    },

    setSidebarCollapsed: function (collapsed) {
        try { localStorage.setItem(this.SIDEBAR_KEY, collapsed ? '1' : '0'); } catch (e) { /* private mode */ }
        document.documentElement.setAttribute('data-sidebar', collapsed ? 'collapsed' : 'expanded');
    },

    getSidebarCollapsed: function () {
        return document.documentElement.getAttribute('data-sidebar') === 'collapsed';
    },

    // The compact breakpoint, read from JS because the C# side cannot see the viewport.
    // Must stay in step with the 860px media query in MainLayout.razor.css — the two
    // together are what make one button mean "collapse the rail" on a wide screen and
    // "open the drawer" on a narrow one.
    isCompact: function () {
        return window.matchMedia('(max-width: 860px)').matches;
    },

    focusElement: function (id) {
        var el = document.getElementById(id);
        if (el) { el.focus(); }
    },

    // Arrow-key movement inside the avatar menu. The menu is buttons rather than a
    // native <select>, so this has to be provided rather than inherited.
    moveMenuFocus: function (delta) {
        var menu = document.querySelector('.appmenu');
        if (!menu) { return; }
        var items = Array.prototype.slice.call(menu.querySelectorAll('button, [href]'))
            .filter(function (el) { return !el.disabled && el.offsetParent !== null; });
        if (!items.length) { return; }
        var current = items.indexOf(document.activeElement);
        var next = current < 0
            ? (delta > 0 ? 0 : items.length - 1)
            : (current + delta + items.length) % items.length;
        items[next].focus();
    },

    // The browser's zone, so the file list can show modified times in local time. Offset is minutes EAST of UTC
    // (the opposite sign to getTimezoneOffset), matching TimeSpan.
    timeZone: function () {
        var id = null;
        try { id = Intl.DateTimeFormat().resolvedOptions().timeZone || null; } catch (e) { /* old browser */ }
        return { id: id, offset: -new Date().getTimezoneOffset() };
    },

    openFilePicker: function (id) {
        var el = document.getElementById(id);
        if (el) { el.click(); }
    }
};

// A 'system' preference has to keep following the OS after the page has loaded, so a
// user who flips their OS theme sees this tab follow without a reload. Only 'system'
// listens; an explicit choice is an explicit choice.
if (window.matchMedia) {
    window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', function () {
        if (window.cssp.getMode() === 'system') {
            document.documentElement.setAttribute('data-bs-theme', window.cssp.resolve('system'));
        }
    });
}
