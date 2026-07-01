// Mock for lightweight-charts — prevents canvas/WebGL init in tests
export function createChart() {
  return {
    remove: () => {},
    resize: () => {},
    timeScale: () => ({ fitContent: () => {} }),
    priceScale: () => ({ applyOptions: () => {} }),
    addCandlestickSeries: () => ({
      setData: () => {},
      priceScale: () => ({ applyOptions: () => {} }),
    }),
    addLineSeries: () => ({
      setData: () => {},
      priceScale: () => ({ applyOptions: () => {} }),
    }),
    addHistogramSeries: () => ({
      setData: () => {},
      priceScale: () => ({ applyOptions: () => {} }),
    }),
    removeSeries: () => {},
  };
}

export const LineType = { Simple: 0, Curved: 1 };
export const ColorType = { Solid: 0 };
export const CrosshairMode = { Normal: 0 };
