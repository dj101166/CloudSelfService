window.themeManager = {
    _key: "cloudSelfServiceTheme",
    _apply: function (mode) {
        const dark = mode === "dark" ||
            (mode === "system" && window.matchMedia("(prefers-color-scheme: dark)").matches);
        document.documentElement.setAttribute("data-theme", dark ? "dark" : "light");
        return dark;
    },
    initializeTheme: function () {
        const mode = localStorage.getItem(this._key) || "system";
        return this._apply(mode);
    },
    setMode: function (mode) {
        localStorage.setItem(this._key, mode);
        return this._apply(mode);
    },
    toggleTheme: function () {
        const isDark = document.documentElement.getAttribute("data-theme") === "dark";
        return this.setMode(isDark ? "light" : "dark");
    }
};
