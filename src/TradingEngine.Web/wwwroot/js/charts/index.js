const chartConfig = {
    layout: { background: { type: 'solid', color: '#0d1117' }, textColor: '#8b949e' },
    grid: { vertLines: { color: '#21262d' }, horzLines: { color: '#21262d' } },
    crosshair: { mode: 0 },
    rightPriceScale: { borderColor: '#30363d' },
    timeScale: { borderColor: '#30363d', timeVisible: true }
};

export function equityChart(container, points) {
    const chart = window.LightweightCharts.createChart(container, {
        ...chartConfig,
        width: container.clientWidth,
        height: container.clientHeight || 300
    });
    const line = chart.addLineSeries({ color: '#58a6ff', lineWidth: 2 });
    const drawdown = chart.addAreaSeries({
        lineColor: '#f85149', topColor: 'rgba(248,81,73,0.4)', bottomColor: 'rgba(248,81,73,0)',
        priceScaleId: 'drawdown'
    });
    chart.priceScale('drawdown').applyOptions({ scaleMargins: { top: 0.8, bottom: 0 } });

    const times = points.map(p => ({ time: p.time, value: p.equity }));
    line.setData(times);

    if (points.length > 0) {
        let peak = points[0].equity;
        const dd = points.map(p => {
            if (p.equity > peak) peak = p.equity;
            return { time: p.time, value: peak > 0 ? -((peak - p.equity) / peak * 100) : 0 };
        });
        drawdown.setData(dd);
    }

    chart.timeScale().fitContent();
    return { chart, line, drawdown, destroy: () => chart.remove() };
}

export function candleChart(container, bars) {
    const chart = window.LightweightCharts.createChart(container, {
        ...chartConfig,
        width: container.clientWidth,
        height: container.clientHeight || 400
    });
    const series = chart.addCandlestickSeries({
        upColor: '#3fb950', downColor: '#f85149', borderVisible: false,
        wickUpColor: '#3fb950', wickDownColor: '#f85149'
    });
    const data = bars.map(b => ({
        time: b.time, open: b.open, high: b.high, low: b.low, close: b.close
    }));
    series.setData(data);
    chart.timeScale().fitContent();

    function addMarkers(markers) {
        const mapped = markers.map(m => ({
            time: m.time, position: m.position || 'inBar', color: m.color || '#58a6ff',
            shape: m.shape || 'circle', text: m.text || ''
        }));
        series.setMarkers(mapped);
    }

    function addPriceLine(label, price, color) {
        series.createPriceLine({ price, color, lineWidth: 1, lineStyle: 2, axisLabelVisible: true, title: label });
    }

    return { chart, series, addMarkers, addPriceLine, destroy: () => chart.remove() };
}

export function histogramChart(container, points, color) {
    const chart = window.LightweightCharts.createChart(container, {
        ...chartConfig,
        width: container.clientWidth,
        height: container.clientHeight || 250
    });
    const series = chart.addHistogramSeries({ color: color || '#58a6ff' });
    series.setData(points);
    chart.timeScale().fitContent();
    return { chart, series, destroy: () => chart.remove() };
}

export function scatterChart(container, points) {
    const chart = window.LightweightCharts.createChart(container, {
        ...chartConfig,
        width: container.clientWidth,
        height: container.clientHeight || 250
    });
    const series = chart.addLineSeries({ color: '#58a6ff', lineWidth: 0, pointMarkersVisible: true });
    series.setData(points);
    chart.timeScale().fitContent();
    return { chart, series, destroy: () => chart.remove() };
}
