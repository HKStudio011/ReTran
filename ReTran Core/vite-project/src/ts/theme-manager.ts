let currentThemeSetting = 'system';

function applyTheme(theme: string) {
    currentThemeSetting = theme;
    const html = document.documentElement;
    const resolvedTheme = theme === 'system'
        ? (window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light')
        : theme;
    html.setAttribute('data-theme', resolvedTheme);
}

function applyColor(colorId: string) {
    const html = document.documentElement;
    if (!colorId || colorId === 'default') {
        html.removeAttribute('data-color');
    } else {
        html.setAttribute('data-color', colorId);
    }
}

window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', (e) => {
    if (currentThemeSetting === 'system') {
        document.documentElement.setAttribute('data-theme', e.matches ? 'dark' : 'light');
    }
});

export { applyTheme, applyColor };
