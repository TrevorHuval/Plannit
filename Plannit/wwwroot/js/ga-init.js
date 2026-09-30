// Google Analytics 4 init. Same-origin file (not inline) so the tag needs no extra CSP allowance
// beyond googletagmanager.com / google-analytics.com. The measurement ID comes from the
// data-ga-id attribute the layout renders, and only when Analytics:MeasurementId is configured.
(function () {
    var tag = document.currentScript;
    var id = tag && tag.getAttribute('data-ga-id');
    if (!id || !/^G-[A-Z0-9]{6,12}$/.test(id)) return;
    if (navigator.doNotTrack === '1' || window.doNotTrack === '1') return;

    window.dataLayer = window.dataLayer || [];
    function gtag() { window.dataLayer.push(arguments); }
    window.gtag = gtag;

    var s = document.createElement('script');
    s.async = true;
    s.src = 'https://www.googletagmanager.com/gtag/js?id=' + id;
    document.head.appendChild(s);

    gtag('js', new Date());
    // Server-rendered pages: the default page_view per load is right, so send_page_view stays on.
    gtag('config', id, {
        allow_google_signals: false,
        allow_ad_personalization_signals: false
    });
    window.gaEnabled = true;
})();
