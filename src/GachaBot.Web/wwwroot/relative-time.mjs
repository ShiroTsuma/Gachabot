export function relativeTime(value, now = Date.now()) {
    const timestamp = Date.parse(value);
    if (!Number.isFinite(timestamp)) return null;
    const seconds = Math.max(0, Math.floor((now - timestamp) / 1000));
    if (seconds < 60) return 'just now';
    if (seconds < 3600) return `${Math.floor(seconds / 60)} min ago`;
    if (seconds < 86400) return `${Math.floor(seconds / 3600)} hr ago`;
    const days = Math.floor(seconds / 86400);
    return `${days} ${days === 1 ? 'day' : 'days'} ago`;
}

if (typeof document !== 'undefined') {
    const format = new Intl.DateTimeFormat('en-GB', {
        day: '2-digit', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit',
    });
    function update() {
        document.querySelectorAll('time[data-relative-time]').forEach(element => {
            const relative = relativeTime(element.dateTime);
            if (relative === null) return;
            const date = new Date(element.dateTime);
            const text = `${format.format(date)} (${relative})`;
            if (element.textContent !== text) element.textContent = text;
            element.title = date.toLocaleString('en-GB', { timeZoneName: 'short' });
        });
    }
    update();
    new MutationObserver(update).observe(document.body, {
        childList: true, subtree: true, attributes: true, attributeFilter: ['datetime'],
    });
    setInterval(update, 30000);
}
