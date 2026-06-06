function renderEquityCurve(canvasId, data) {
    const ctx = document.getElementById(canvasId);
    if (!ctx || !data || data.length === 0) return;
    new Chart(ctx, {
        type: 'line',
        data: {
            labels: data.map(d => new Date(d.timestamp).toLocaleDateString()),
            datasets: [{
                label: 'Equity',
                data: data.map(d => d.equity),
                borderColor: '#58a6ff',
                backgroundColor: 'rgba(88, 166, 255, 0.1)',
                fill: true,
                tension: 0.1
            }]
        },
        options: {
            responsive: true,
            plugins: { legend: { display: false } },
            scales: {
                x: { ticks: { color: '#8b949e' } },
                y: { ticks: { color: '#8b949e' } }
            }
        }
    });
}
