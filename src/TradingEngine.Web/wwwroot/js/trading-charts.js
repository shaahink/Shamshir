const _charts = {};

export function createOhlcChart(elementId) {
    if (_charts[elementId]) destroyChart(elementId);
    const container = document.getElementById(elementId);
    if (!container) return;
    const chart = LightweightCharts.createChart(container, {
        width: container.clientWidth, height: 400,
        layout: { background: { type: 'solid', color: '#1a1a2e' }, textColor: '#d1d4dc' },
        grid: { vertLines: { color: '#2a2a3e' }, horzLines: { color: '#2a2a3e' } },
        crosshair: { mode: 0 },
        timeScale: { borderColor: '#4a4a5e', timeVisible: true },
    });
    _charts[elementId] = { chart, series: chart.addCandlestickSeries({ upColor: '#26a69a', downColor: '#ef5350' }) };
}

export function setOhlcData(elementId, bars) {
    const e = _charts[elementId]; if (!e) return;
    e.series.setData(bars.map(b => ({ time: b.time, open: b.open, high: b.high, low: b.low, close: b.close })));
}

export function addTradeMarkers(elementId, entry, exit, sl, tp) {
    const e = _charts[elementId]; if (!e) return;
    const markers = [];
    if (entry) markers.push({ time: entry.time, position: 'belowBar', color: entry.dir === 'Long' ? '#26a69a' : '#ef5350', shape: entry.dir === 'Long' ? 'arrowUp' : 'arrowDown', text: 'E' });
    if (exit) markers.push({ time: exit.time, position: 'aboveBar', color: '#ff9800', shape: 'circle', text: 'X' });
    if (markers.length) e.series.setMarkers(markers);
}

export function createEquityChart(elementId) {
    if (_charts[elementId]) destroyChart(elementId);
    const container = document.getElementById(elementId);
    if (!container) return;
    const chart = LightweightCharts.createChart(container, {
        width: container.clientWidth, height: 300,
        layout: { background: { type: 'solid', color: '#1a1a2e' }, textColor: '#d1d4dc' },
        grid: { vertLines: { color: '#2a2a3e' }, horzLines: { color: '#2a2a3e' } },
        timeScale: { borderColor: '#4a4a5e', timeVisible: true },
    });
    _charts[elementId] = { chart, series: chart.addLineSeries({ color: '#26a69a', lineWidth: 2 }) };
}

export function setEquityData(elementId, data) {
    const e = _charts[elementId]; if (!e) return;
    e.series.setData(data.map(d => ({ time: d.time, value: d.value })));
}

export function createMultiEquityChart(elementId) {
    if (_charts[elementId]) destroyChart(elementId);
    const container = document.getElementById(elementId);
    if (!container) return;
    const chart = LightweightCharts.createChart(container, {
        width: container.clientWidth, height: 400,
        layout: { background: { type: 'solid', color: '#1a1a2e' }, textColor: '#d1d4dc' },
        timeScale: { borderColor: '#4a4a5e', timeVisible: true },
    });
    _charts[elementId] = { chart, series: {} };
}

export function addMultiEquitySeries(elementId, key, data, color) {
    const e = _charts[elementId]; if (!e) return;
    const s = e.chart.addLineSeries({ color: color || '#26a69a', lineWidth: 2 });
    s.setData(data.map(d => ({ time: d.time, value: d.value })));
    e.series[key] = s;
}

export function destroyChart(elementId) {
    const e = _charts[elementId];
    if (e) { e.chart.remove(); delete _charts[elementId]; }
}
