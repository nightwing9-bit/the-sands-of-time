namespace Revenant.Tests.Intelligence;

internal static partial class R5Experiment
{
    // Fit in standardized-score coordinates using damped Newton steps with a fixed weak slope penalty.
    // The exported coefficients apply directly to the raw ML.NET Score, not its Probability column.
    private static (double Slope, double Intercept) FitSigmoid((double Score, bool Positive)[] rows)
    {
        var mean = rows.Average(x => x.Score);
        var scale = Math.Sqrt(rows.Average(x => Math.Pow(x.Score - mean, 2)));
        if (!double.IsFinite(scale) || scale < 1e-9) throw new InvalidOperationException("Calibration scores have no usable spread.");
        var prevalence = (double)rows.Count(x => x.Positive) / rows.Length;
        if (prevalence is <= 0 or >= 1) throw new InvalidOperationException("Calibration requires both classes.");
        var data = rows.Select(r => (X: (r.Score - mean) / scale, Y: r.Positive ? 1d : 0d)).ToArray();
        var intercept = Math.Log(prevalence / (1 - prevalence));
        var slope = 0d;
        const double ridge = .01;
        for (var iter = 0; iter < 80; iter++)
        {
            double ga = 0, gb = ridge * slope, aa = 0, ab = 0, bb = ridge;
            foreach (var (x, y) in data)
            {
                var p = Sigmoid(slope * x + intercept);
                var w = p * (1 - p);
                ga += p - y;
                gb += (p - y) * x;
                aa += w;
                ab += w * x;
                bb += w * x * x;
            }
            var det = aa * bb - ab * ab;
            if (!double.IsFinite(det) || det <= 0) throw new InvalidOperationException("Singular sigmoid calibration.");
            var da = (bb * ga - ab * gb) / det;
            var db = (aa * gb - ab * ga) / det;
            if (Math.Max(Math.Abs(da), Math.Abs(db)) < 1e-8) break;
            var before = Loss(slope, intercept);
            var advanced = false;
            for (var step = 1d; step >= 1d / 4096; step /= 2)
            {
                var a = intercept - step * da;
                var b = Math.Max(0, slope - step * db);
                if (Loss(b, a) < before)
                {
                    intercept = a;
                    slope = b;
                    advanced = true;
                    break;
                }
            }
            if (!advanced) break;
        }
        if (slope <= 1e-8) throw new InvalidOperationException("Calibration does not support an increasing score-to-probability map.");
        var rawSlope = slope / scale;
        return (rawSlope, intercept - rawSlope * mean);

        double Loss(double b, double a)
        {
            var loss = ridge * b * b / 2;
            foreach (var (x, y) in data)
            {
                var signed = (y == 1 ? -1 : 1) * (b * x + a);
                loss += Math.Max(0, signed) + Math.Log(1 + Math.Exp(-Math.Abs(signed)));
            }
            return loss;
        }
    }

    private static double Sigmoid(double score)
    {
        if (!double.IsFinite(score)) throw new InvalidOperationException("Nonfinite calibration logit.");
        var exponent = Math.Exp(-Math.Abs(score));
        return score >= 0 ? 1 / (1 + exponent) : exponent / (1 + exponent);
    }
}
