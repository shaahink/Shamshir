"""Stationary block bootstrap + MDE calculator (iter-viability V5 tooling, pre-delivered).

The D5' survival gate and every V2+ family verdict need a dependence-respecting CI on pooled
dollars; D1 requires an MDE line in every pre-registration. This module provides both, stdlib
only, importable by other tools/research scripts or run standalone.

Method: Politis-Romano stationary bootstrap — resample by concatenating blocks whose start is
uniform and whose length is geometric with mean L (wrap-around), preserving short-range serial
dependence that an iid bootstrap destroys.

Gate GV5 discipline: `--selftest` reproduces hand-checkable synthetic cases —
  (a) iid N(0,1): bootstrap SE of the mean must match sigma/sqrt(n);
  (b) AR(1) phi=0.6: true SE of the mean is sqrt((1+phi)/(1-phi)) = 2x the iid formula; the
      block bootstrap must land near it while the naive iid bootstrap must UNDERSTATE it.

Usage:
  python block_bootstrap.py --selftest
  from block_bootstrap import stationary_bootstrap_se, mde
"""
import argparse
import math
import random
import statistics


def stationary_bootstrap(series, stat, reps=2000, mean_block=10, seed=20260716):
    """Return the bootstrap distribution of stat(resampled series)."""
    n = len(series)
    if n < 2:
        raise ValueError("need at least 2 observations")
    rng = random.Random(seed)
    p = 1.0 / mean_block
    out = []
    for _ in range(reps):
        sample = []
        i = rng.randrange(n)
        while len(sample) < n:
            sample.append(series[i])
            if rng.random() < p:
                i = rng.randrange(n)   # start a new block
            else:
                i = (i + 1) % n        # continue the block, wrap around
        out.append(stat(sample))
    out.sort()
    return out


def stationary_bootstrap_se(series, stat=statistics.fmean, reps=2000, mean_block=10, seed=20260716):
    """(se, ci_lo, ci_hi) of the statistic under the stationary bootstrap (95% percentile CI)."""
    dist = stationary_bootstrap(series, stat, reps, mean_block, seed)
    se = statistics.stdev(dist)
    return se, dist[int(0.025 * len(dist))], dist[int(0.975 * len(dist)) - 1]


def mde(se, alpha=0.05, power=0.8):
    """Minimum detectable effect for a two-sided test at the given size and power (D1).

    Defaults give the familiar (1.96 + 0.8416) x SE = 2.80 x SE.
    """
    z_alpha = _z(1 - alpha / 2)
    z_power = _z(power)
    return (z_alpha + z_power) * se


def _z(q):
    """Inverse standard-normal CDF (Acklam's rational approximation; |err| < 1.2e-9)."""
    if not 0 < q < 1:
        raise ValueError(q)
    a = [-3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02,
         1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00]
    b = [-5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02,
         6.680131188771972e+01, -1.328068155288572e+01]
    c = [-7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00,
         -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00]
    d = [7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00,
         3.754408661907416e+00]
    plow, phigh = 0.02425, 1 - 0.02425
    if q < plow:
        u = math.sqrt(-2 * math.log(q))
        return (((((c[0] * u + c[1]) * u + c[2]) * u + c[3]) * u + c[4]) * u + c[5]) / \
               ((((d[0] * u + d[1]) * u + d[2]) * u + d[3]) * u + 1)
    if q > phigh:
        u = math.sqrt(-2 * math.log(1 - q))
        return -(((((c[0] * u + c[1]) * u + c[2]) * u + c[3]) * u + c[4]) * u + c[5]) / \
               ((((d[0] * u + d[1]) * u + d[2]) * u + d[3]) * u + 1)
    u = q - 0.5
    t = u * u
    return (((((a[0] * t + a[1]) * t + a[2]) * t + a[3]) * t + a[4]) * t + a[5]) * u / \
           (((((b[0] * t + b[1]) * t + b[2]) * t + b[3]) * t + b[4]) * t + 1)


def _iid_bootstrap_se(series, reps=2000, seed=20260716):
    rng = random.Random(seed)
    n = len(series)
    means = []
    for _ in range(reps):
        means.append(statistics.fmean(rng.choice(series) for _ in range(n)))
    return statistics.stdev(means)


def selftest():
    ok = True

    # (a) iid N(0,1), n=2000: SE of the mean must be ~ 1/sqrt(2000) = 0.02236.
    rng = random.Random(42)
    iid = [rng.gauss(0, 1) for _ in range(2000)]
    theory_iid = 1 / math.sqrt(2000)
    se_iid, _, _ = stationary_bootstrap_se(iid, mean_block=10)
    err_a = abs(se_iid - theory_iid) / theory_iid
    print(f"(a) iid N(0,1) n=2000: block-bootstrap SE={se_iid:.5f}  theory={theory_iid:.5f}  err={err_a:.1%}")
    ok &= err_a < 0.15

    # (b) AR(1) phi=0.6, marginal sigma=1: true SE of the mean = sqrt((1+phi)/(1-phi))/sqrt(n) = 2x iid.
    phi = 0.6
    sigma_e = math.sqrt(1 - phi * phi)
    x = 0.0
    ar = []
    for _ in range(2000):
        x = phi * x + rng.gauss(0, sigma_e)
        ar.append(x)
    theory_ar = math.sqrt((1 + phi) / (1 - phi)) / math.sqrt(len(ar))
    se_block, _, _ = stationary_bootstrap_se(ar, mean_block=25)
    se_naive = _iid_bootstrap_se(ar)
    err_b = abs(se_block - theory_ar) / theory_ar
    print(f"(b) AR(1) phi=0.6 n=2000: block SE={se_block:.5f}  theory={theory_ar:.5f}  err={err_b:.1%}"
          f"  | naive iid SE={se_naive:.5f} (must understate)")
    ok &= err_b < 0.20
    ok &= se_naive < 0.75 * se_block

    # (c) MDE arithmetic: 2.8016 x SE at alpha=.05, power=.8; z-approximation sanity.
    m = mde(1.0)
    print(f"(c) MDE(SE=1) = {m:.4f}  (expected 2.8016);  z(0.975)={_z(0.975):.4f} (expected 1.9600)")
    ok &= abs(m - 2.8016) < 0.001 and abs(_z(0.975) - 1.96) < 0.001

    print("SELFTEST", "PASS" if ok else "FAIL")
    return 0 if ok else 1


ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
ap.add_argument("--selftest", action="store_true")
args = ap.parse_args()
if args.selftest:
    raise SystemExit(selftest())
ap.print_help()
