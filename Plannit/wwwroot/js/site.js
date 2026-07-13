(function () {
    'use strict';

    // ===== Theme Toggle =====

    function getPreferredTheme() {
        var stored = localStorage.getItem('pl-theme');
        if (stored === 'light' || stored === 'dark') return stored;
        return window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
    }

    function applyTheme(theme) {
        document.documentElement.setAttribute('data-bs-theme', theme);
        var icon = document.getElementById('themeIcon');
        var label = document.getElementById('themeLabel');
        if (icon) icon.className = theme === 'dark' ? 'bi bi-sun' : 'bi bi-moon-stars';
        if (label) label.textContent = theme === 'dark' ? 'Light Mode' : 'Dark Mode';
        document.dispatchEvent(new CustomEvent('themechanged', { detail: { theme: theme } }));
    }

    window.PlTheme = {
        toggle: function () {
            var current = document.documentElement.getAttribute('data-bs-theme') || 'light';
            var next = current === 'dark' ? 'light' : 'dark';
            localStorage.setItem('pl-theme', next);
            applyTheme(next);
        },
        current: function () {
            return document.documentElement.getAttribute('data-bs-theme') || 'light';
        }
    };

    applyTheme(getPreferredTheme());

    window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', function () {
        if (!localStorage.getItem('pl-theme')) {
            applyTheme(getPreferredTheme());
        }
    });

    // ===== Sidebar =====

    function initSidebar() {
        var sidebar = document.getElementById('plSidebar');
        var backdrop = document.getElementById('plBackdrop');
        var hamburger = document.getElementById('plHamburger');
        var collapseBtn = document.getElementById('plCollapseBtn');

        if (!sidebar) return;

        if (localStorage.getItem('pl-sidebar-collapsed') === 'true') {
            sidebar.classList.add('collapsed');
        }

        if (hamburger) {
            hamburger.addEventListener('click', function () {
                sidebar.classList.toggle('mobile-open');
            });
        }

        if (backdrop) {
            backdrop.addEventListener('click', function () {
                sidebar.classList.remove('mobile-open');
            });
        }

        if (collapseBtn) {
            collapseBtn.addEventListener('click', function () {
                sidebar.classList.toggle('collapsed');
                localStorage.setItem('pl-sidebar-collapsed', sidebar.classList.contains('collapsed'));
            });
        }

        sidebar.querySelectorAll('.pl-nav-link').forEach(function (link) {
            link.addEventListener('click', function () {
                sidebar.classList.remove('mobile-open');
            });
        });
    }

    // ===== Chart.js Theme Helpers =====

    function getChartColors() {
        var style = getComputedStyle(document.documentElement);
        return {
            text: style.getPropertyValue('--pl-text').trim(),
            textMuted: style.getPropertyValue('--pl-text-muted').trim(),
            border: style.getPropertyValue('--pl-border').trim(),
            borderLight: style.getPropertyValue('--pl-border-light').trim(),
            primary: style.getPropertyValue('--pl-primary').trim(),
            positive: style.getPropertyValue('--pl-positive').trim(),
            negative: style.getPropertyValue('--pl-negative').trim(),
            surface: style.getPropertyValue('--pl-surface').trim()
        };
    }

    window.PlChart = {
        getColors: getChartColors,
        // Warm editorial series palette — bronzes, greens, slate, clay ("The Private Ledger")
        palette: [
            '#7e5e38', '#2f6b45', '#4a6b8a', '#b07a2b', '#9e6b52',
            '#6f8f6a', '#8a6d9c', '#c79a5a', '#5a7d8c', '#a8895f',
            '#3f6b57', '#9e3b2e', '#7a7259'
        ],
        applyTheme: function (chart) {
            if (!chart) return;
            var c = getChartColors();
            if (chart.options.scales) {
                ['x', 'y'].forEach(function (axis) {
                    var scale = chart.options.scales[axis];
                    if (scale) {
                        if (!scale.ticks) scale.ticks = {};
                        scale.ticks.color = c.textMuted;
                        if (!scale.grid) scale.grid = {};
                        scale.grid.color = c.borderLight;
                        if (scale.title) scale.title.color = c.textMuted;
                    }
                });
            }
            if (chart.options.plugins && chart.options.plugins.legend) {
                if (!chart.options.plugins.legend.labels) chart.options.plugins.legend.labels = {};
                chart.options.plugins.legend.labels.color = c.textMuted;
            }
            chart.update('none');
        },
        defaultScaleOptions: function () {
            var c = getChartColors();
            return {
                ticks: { color: c.textMuted },
                grid: { color: c.borderLight }
            };
        }
    };

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', initSidebar);
    } else {
        initSidebar();
    }
})();
