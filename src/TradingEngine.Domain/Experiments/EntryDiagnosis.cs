namespace TradingEngine.Domain.Experiments;

/// <summary>
/// P6.7 — entry-quality decomposition: for each strategy (or run), regress OOS trade R on observable-at-entry
/// features (ATR percentile, session, distance-to-EMA in ATR, squeeze age…). Not for prediction — for
/// diagnosis: it tells you WHICH conditions your entries pay in, feeding filter design by evidence.
/// Plain OLS on a handful of features; explainable, no external library dependency.
/// </summary>

/// <summary>One trade's observable-at-entry features + outcome (R-multiple).</summary>
public sealed record EntryObservation(
    double RMultiple,
    double AtrPercentile,
    double EmaDistanceAtr,
    int SqueezeAge,
    string Session,
    string StrategyId,
    string Symbol,
    string Timeframe);

/// <summary>OLS regression feature — what was regressed and its coefficient.</summary>
public sealed record EntryDiagnosisFeature(
    string Name,
    double Coefficient,
    double StandardError,
    double TStatistic);

/// <summary>Result of a single regression: RMultiple ~ β₀ + β₁·X₁ + β₂·X₂ + …</summary>
public sealed record EntryDiagnosisResult(
    IReadOnlyList<EntryDiagnosisFeature> Features,
    double Intercept,
    double RSquared,
    double AdjustedRSquared,
    int Observations,
    int Parameters,
    string Summary);

public static class EntryDiagnosis
{
    /// <summary>
    /// Run OLS regression: RMultiple ~ AtrPercentile + EmaDistanceAtr + SqueezeAge + session dummies.
    /// Returns a diagnosis result with per-feature coefficients, standard errors, t-statistics,
    /// and R-squared — all auditable, no ML.
    /// </summary>
    public static EntryDiagnosisResult Diagnose(IReadOnlyList<EntryObservation> observations)
    {
        if (observations.Count < 3)
        {
            return new EntryDiagnosisResult([], 0, 0, 0, observations.Count, 0,
                $"Insufficient observations ({observations.Count}); need ≥3 for regression.");
        }

        var sessions = observations
            .Select(o => o.Session)
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        var featureNames = new List<string> { "AtrPercentile", "EmaDistanceAtr", "SqueezeAge" };
        if (sessions.Count > 1)
        {
            featureNames.AddRange(sessions.Skip(1).Select(s => $"Session_{s}"));
        }

        var n = observations.Count;
        var fullK = featureNames.Count + 1;

        // Build full design matrix X: n × fullK, first column all 1s (intercept)
        var y = new double[n];
        var X = new double[n, fullK];

        for (var i = 0; i < n; i++)
        {
            var o = observations[i];
            y[i] = o.RMultiple;

            X[i, 0] = 1.0;
            X[i, 1] = o.AtrPercentile;
            X[i, 2] = o.EmaDistanceAtr;
            X[i, 3] = o.SqueezeAge;

            for (var j = 1; j < sessions.Count; j++)
            {
                X[i, 4 + (j - 1)] = o.Session.Equals(sessions[j], StringComparison.Ordinal) ? 1.0 : 0.0;
            }
        }

        // drop features with zero variance (they cause singular design matrix)
        var activeFeatures = new List<int>();
        for (var fi = 0; fi < featureNames.Count; fi++)
        {
            var fIdx = fi + 1;
            var first = X[0, fIdx];
            var hasVariance = false;
            for (var r = 1; r < n; r++)
            {
                if (Math.Abs(X[r, fIdx] - first) > 1e-12)
                {
                    hasVariance = true;
                    break;
                }
            }
            if (hasVariance) activeFeatures.Add(fi);
        }

        var k = activeFeatures.Count + 1; // +1 for intercept

        if (n <= k)
        {
            return new EntryDiagnosisResult([], 0, 0, 0, observations.Count, k,
                $"Insufficient observations ({observations.Count}) for {k} parameters; need >{k}.");
        }

        if (k <= 1)
        {
            return new EntryDiagnosisResult([], 0, 0, 0, observations.Count, k,
                "No features to regress on; add at least one feature or session with variance.");
        }

        // Build compact design matrix — only active features + intercept
        var Xcompact = new double[n, k];
        for (var i = 0; i < n; i++)
        {
            Xcompact[i, 0] = 1.0;
            var col = 1;
            for (var ai = 0; ai < activeFeatures.Count; ai++)
            {
                var fi = activeFeatures[ai];
                Xcompact[i, col++] = X[i, fi + 1];
            }
        }

        // OLS on compact matrix: β = (XᵀX)⁻¹Xᵀy
        var XtX = MatrixMultiplyTransposeLeft(Xcompact, n, k);
        var XtXInv = InvertMatrix(XtX, k);
        if (XtXInv is null)
        {
            return new EntryDiagnosisResult([], 0, 0, 0, observations.Count, k,
                "Design matrix is singular — cannot compute regression. Try more observations or fewer features.");
        }

        var Xty = MatrixMultiplyTransposeVector(Xcompact, y, n, k);
        var beta = MatrixMultiplyVector(XtXInv, Xty, k);

        // compute fitted values and residuals
        var fitted = new double[n];
        var residuals = new double[n];
        for (var i = 0; i < n; i++)
        {
            var f = 0.0;
            for (var j = 0; j < k; j++) f += Xcompact[i, j] * beta[j];
            fitted[i] = f;
            residuals[i] = y[i] - f;
        }

        // R-squared
        var yMean = y.Average();
        var ssTotal = y.Sum(yi => (yi - yMean) * (yi - yMean));
        var ssResidual = residuals.Sum(r => r * r);
        var rSquared = ssTotal > 0 ? 1.0 - ssResidual / ssTotal : 0.0;
        var adjustedRSquared = n > k
            ? 1.0 - (1.0 - rSquared) * (n - 1.0) / (n - k)
            : rSquared;

        // Standard errors: σ² = residuals² / (n - k),  SE(βⱼ) = σ × √((XᵀX)⁻¹ⱼⱼ)
        var sigmaSquared = ssResidual / (n - k);
        var sigma = Math.Sqrt(sigmaSquared);

        var features = new List<EntryDiagnosisFeature>();
        features.Add(new EntryDiagnosisFeature("Intercept", beta[0],
            sigma * Math.Sqrt(XtXInv[0, 0]),
            sigma > 0 ? beta[0] / (sigma * Math.Sqrt(XtXInv[0, 0])) : 0));

        var activeNames = new List<string>();
        foreach (var ai in activeFeatures)
            activeNames.Add(featureNames[ai]);

        for (var j = 0; j < activeNames.Count; j++)
        {
            var se = sigma * Math.Sqrt(XtXInv[j + 1, j + 1]);
            var t = se > 0 ? beta[j + 1] / se : 0;
            features.Add(new EntryDiagnosisFeature(activeNames[j], beta[j + 1], se, t));
        }

        var summary = $"OLS R²={rSquared:F3} (adj={adjustedRSquared:F3}), n={n}, p={k}. ";

        // highlight top 3 features by abs(t-stat)
        var top3 = features
            .Where(f => f.Name != "Intercept")
            .OrderByDescending(f => Math.Abs(f.TStatistic))
            .Take(3)
            .ToList();

        if (top3.Count > 0)
        {
            var positive = top3.Where(f => f.Coefficient > 0).ToList();
            var negative = top3.Where(f => f.Coefficient < 0).ToList();
            if (positive.Count > 0)
                summary += $"Positive: {string.Join(", ", positive.Select(f => $"{f.Name} (+{f.Coefficient:F3}, t={f.TStatistic:F1})"))}. ";
            if (negative.Count > 0)
                summary += $"Negative: {string.Join(", ", negative.Select(f => $"{f.Name} ({f.Coefficient:F3}, t={f.TStatistic:F1})"))}. ";
        }
        else
        {
            summary += "No features with measurable signal — entries are indistinguishable from random on these dimensions.";
        }

        return new EntryDiagnosisResult(features, beta[0], rSquared, adjustedRSquared, n, k, summary.TrimEnd());
    }

    /// <summary>Compute ATR from bars over a window.</summary>
    public static double ComputeAtr(IReadOnlyList<double> high, IReadOnlyList<double> low, IReadOnlyList<double> close, int period)
    {
        var n = Math.Min(high.Count, Math.Min(low.Count, close.Count));
        if (n < 2 || period < 1) return 0.0;

        var tr = new List<double>();
        for (var i = 1; i < n; i++)
        {
            var h = high[i];
            var l = low[i];
            var prevClose = close[i - 1];
            var tr1 = h - l;
            var tr2 = Math.Abs(h - prevClose);
            var tr3 = Math.Abs(l - prevClose);
            tr.Add(Math.Max(tr1, Math.Max(tr2, tr3)));
        }

        if (tr.Count == 0) return 0.0;

        var window = Math.Min(period, tr.Count);
        var sum = 0.0;
        for (var i = tr.Count - window; i < tr.Count; i++) sum += tr[i];
        return sum / window;
    }

    /// <summary>Compute EMA over the close series.</summary>
    public static double ComputeEma(IReadOnlyList<double> close, int period)
    {
        if (close.Count == 0 || period < 1) return 0.0;
        var multiplier = 2.0 / (period + 1);

        // seed with SMA over first 'period' values
        var seed = 0.0;
        var seedCount = Math.Min(period, close.Count);
        for (var i = close.Count - seedCount; i < close.Count; i++) seed += close[i];
        var ema = seed / seedCount;

        // iterate forward from seed index
        var start = Math.Max(0, close.Count - seedCount);
        for (var i = start; i < close.Count; i++)
        {
            ema = (close[i] - ema) * multiplier + ema;
        }

        return ema;
    }

    /// <summary>Compute SMA over the close series.</summary>
    public static double ComputeSma(IReadOnlyList<double> close, int period)
    {
        if (close.Count < period) return 0.0;
        var sum = 0.0;
        for (var i = close.Count - period; i < close.Count; i++) sum += close[i];
        return sum / period;
    }

    /// <summary>Compute squeeze age: number of consecutive bars where Bollinger width percentile is below the given quantile.</summary>
    public static int ComputeSqueezeAge(IReadOnlyList<double> high, IReadOnlyList<double> low, IReadOnlyList<double> close, int bbPeriod, int lookback)
    {
        var n = Math.Min(high.Count, Math.Min(low.Count, close.Count));
        if (n < bbPeriod + lookback) return 0;

        // compute BB width for each bar in the lookback window
        var windows = Math.Min(lookback, n - bbPeriod);
        var widths = new List<double>();
        for (var i = n - windows; i <= n; i++)
        {
            var end = Math.Min(i, n);
            var start = Math.Max(0, end - bbPeriod);
            if (end - start < bbPeriod) continue;
            var sma = 0.0;
            for (var j = start; j < end; j++) sma += close[j];
            sma /= bbPeriod;

            var variance = 0.0;
            for (var j = start; j < end; j++)
            {
                var diff = close[j] - sma;
                variance += diff * diff;
            }
            variance /= bbPeriod;
            var std = Math.Sqrt(variance);

            // Standard BB width: (upper - lower) / middle = 4*std / sma
            // This is scale-invariant: a quiet market has narrow bands, a volatile one has wide bands.
            var width = sma > 0 ? (2 * std) / sma : 0;
            widths.Add(width);
        }

        if (widths.Count == 0) return 0;

        // find percentile threshold: is the most recent width in the bottom quartile?
        var sorted = widths.OrderBy(w => w).ToList();
        var threshold = sorted[Math.Max(0, sorted.Count / 4)]; // 25th percentile

        // count consecutive bars from most recent where width ≤ threshold
        var age = 0;
        for (var i = widths.Count - 1; i >= 0; i--)
        {
            if (widths[i] <= threshold) age++;
            else break;
        }

        return age;
    }

    // --- matrix helpers (plain, no external deps) ---

    private static double[,] MatrixMultiplyTransposeLeft(double[,] X, int rows, int cols)
    {
        var result = new double[cols, cols];
        for (var i = 0; i < cols; i++)
        {
            for (var j = 0; j < cols; j++)
            {
                var sum = 0.0;
                for (var r = 0; r < rows; r++)
                {
                    sum += X[r, i] * X[r, j];
                }
                result[i, j] = sum;
            }
        }
        return result;
    }

    private static double[] MatrixMultiplyTransposeVector(double[,] X, double[] y, int rows, int cols)
    {
        var result = new double[cols];
        for (var j = 0; j < cols; j++)
        {
            var sum = 0.0;
            for (var r = 0; r < rows; r++)
            {
                sum += X[r, j] * y[r];
            }
            result[j] = sum;
        }
        return result;
    }

    private static double[] MatrixMultiplyVector(double[,] M, double[] v, int n)
    {
        var result = new double[n];
        for (var i = 0; i < n; i++)
        {
            var sum = 0.0;
            for (var j = 0; j < n; j++)
            {
                sum += M[i, j] * v[j];
            }
            result[i] = sum;
        }
        return result;
    }

    /// <summary>In-place Gauss-Jordan matrix inversion. Returns null if singular.</summary>
    private static double[,]? InvertMatrix(double[,] M, int n)
    {
        // augment with identity
        var aug = new double[n, 2 * n];
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < n; j++)
            {
                aug[i, j] = M[i, j];
            }
        }

        for (var i = 0; i < n; i++)
        {
            aug[i, n + i] = 1.0;
        }

        for (var col = 0; col < n; col++)
        {
            // find pivot
            var pivotRow = col;
            var pivotVal = Math.Abs(aug[col, col]);
            for (var r = col + 1; r < n; r++)
            {
                if (Math.Abs(aug[r, col]) > pivotVal)
                {
                    pivotVal = Math.Abs(aug[r, col]);
                    pivotRow = r;
                }
            }

            if (pivotVal < 1e-14) return null; // singular

            // swap rows
            if (pivotRow != col)
            {
                for (var j = 0; j < 2 * n; j++)
                {
                    (aug[col, j], aug[pivotRow, j]) = (aug[pivotRow, j], aug[col, j]);
                }
            }

            // normalize pivot row
            var pivot = aug[col, col];
            for (var j = 0; j < 2 * n; j++)
            {
                aug[col, j] /= pivot;
            }

            // eliminate other rows
            for (var r = 0; r < n; r++)
            {
                if (r == col) continue;
                var factor = aug[r, col];
                for (var j = 0; j < 2 * n; j++)
                {
                    aug[r, j] -= factor * aug[col, j];
                }
            }
        }

        var inv = new double[n, n];
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < n; j++)
            {
                inv[i, j] = aug[i, n + j];
            }
        }

        return inv;
    }
}
