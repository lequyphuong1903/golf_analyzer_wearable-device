using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using OxyPlot.Annotations;
using System;
using System.Collections.Generic;

namespace GolfAnalyzer.Models
{
    public class ChartModel
    {
        private readonly TwoColorAreaSeries _series1;
        private readonly TwoColorAreaSeries _series2;
        private readonly TwoColorAreaSeries _series3;

        private readonly PointAnnotation _cursorPoint1;
        private readonly PointAnnotation _cursorPoint2;
        private readonly PointAnnotation _cursorPoint3;

        private int MaxPoints { get; set; } = 700;
        private int _writeIndex = 0;

        public PlotModel PlotValue1 { get; }
        public PlotModel PlotValue2 { get; }
        public PlotModel PlotValue3 { get; }

        public ChartModel()
        {
            PlotValue1 = CreatePlot();
            PlotValue2 = CreatePlot();
            PlotValue3 = CreatePlot();

            // Colors: pink, yellow, blue
            var pink = OxyColor.FromRgb(255, 105, 180); // HotPink-like
            var yellow = OxyColors.Yellow;
            var blue = OxyColor.FromRgb(0, 160, 255);   // Bright blue

            _series1 = CreateTwoColorAreaSeries(pink);
            _series2 = CreateTwoColorAreaSeries(yellow);
            _series3 = CreateTwoColorAreaSeries(blue);

            PlotValue1.Series.Add(_series1);
            PlotValue2.Series.Add(_series2);
            PlotValue3.Series.Add(_series3);

            // Cursor points only (no vertical lines)
            _cursorPoint1 = CreateCursorPoint(pink);
            _cursorPoint2 = CreateCursorPoint(yellow);
            _cursorPoint3 = CreateCursorPoint(blue);

            PlotValue1.Annotations.Add(_cursorPoint1);
            PlotValue2.Annotations.Add(_cursorPoint2);
            PlotValue3.Annotations.Add(_cursorPoint3);

            // Start hidden (no position yet)
            HideCursor();
        }

        private static TwoColorAreaSeries CreateTwoColorAreaSeries(OxyColor baseColor)
        {
            return new TwoColorAreaSeries
            {
                StrokeThickness = 2,
                LineJoin = LineJoin.Round,
                // Use the same hue above and below the zero line
                Color = baseColor,
                Color2 = baseColor,
                Fill = WithAlpha(baseColor, 96),
                Fill2 = WithAlpha(baseColor, 96),
                ConstantY2 = 0,
                Limit = 0,
                InterpolationAlgorithm = new CanonicalSpline(0.6)
            };
        }

        private PlotModel CreatePlot()
        {
            var bg = OxyColors.Black;

            var pm = new PlotModel
            {
                PlotAreaBorderThickness = new OxyThickness(0),
                Background = bg,
                PlotAreaBackground = bg,
                TextColor = OxyColors.White
            };

            var xAxis = new LinearAxis
            {
                Position = AxisPosition.Bottom,
                IsPanEnabled = false,
                IsZoomEnabled = false,
                Minimum = 0,
                Maximum = MaxPoints - 1,
                MajorGridlineStyle = LineStyle.Solid,
                MajorGridlineColor = WithAlpha(OxyColors.White, 24),
                MinorGridlineStyle = LineStyle.None,
                AxislineStyle = LineStyle.None,
                TicklineColor = WithAlpha(OxyColors.White, 120),
                TextColor = OxyColors.White
            };

            var yAxis = new LinearAxis
            {
                Position = AxisPosition.Left,
                IsPanEnabled = false,
                IsZoomEnabled = false,
                MajorGridlineStyle = LineStyle.Solid,
                MajorGridlineColor = WithAlpha(OxyColors.White, 24),
                MinorGridlineStyle = LineStyle.None,
                TicklineColor = WithAlpha(OxyColors.White, 120),
                TextColor = OxyColors.White
            };

            pm.Axes.Add(xAxis);
            pm.Axes.Add(yAxis);

            pm.Annotations.Add(new LineAnnotation
            {
                Type = LineAnnotationType.Horizontal,
                Y = 0,
                Color = WithAlpha(OxyColors.White, 48),
                StrokeThickness = 1
            });

            return pm;
        }

        public void AddData(float v1, float v2, float v3)
        {
            _series1.Points.Add(new DataPoint(_writeIndex, v1));
            _series2.Points.Add(new DataPoint(_writeIndex, v2));
            _series3.Points.Add(new DataPoint(_writeIndex, v3));

            _writeIndex++;
            if (_writeIndex >= MaxPoints)
                _writeIndex = 0;
        }

        public void Reset()
        {
            _series1.Points.Clear();
            _series2.Points.Clear();
            _series3.Points.Clear();
            PlotValue1.InvalidatePlot(true);
            PlotValue2.InvalidatePlot(true);
            PlotValue3.InvalidatePlot(true);
        }

        // Existing: apply the same bands to all 3 plots
        public void SetPhaseBands(IEnumerable<(double fromX, double toX, OxyColor color, string? label)> bands)
        {
            ApplyBands(PlotValue1, bands);
            ApplyBands(PlotValue2, bands);
            ApplyBands(PlotValue3, bands);
        }

        // New: apply bands to a specific plot
        public void SetPhaseBands(PlotModel pm, IEnumerable<(double fromX, double toX, OxyColor color, string? label)> bands)
        {
            ApplyBands(pm, bands);
        }

        private static void ApplyBands(PlotModel pm, IEnumerable<(double fromX, double toX, OxyColor color, string? label)> bands)
        {
            for (int i = pm.Annotations.Count - 1; i >= 0; i--)
            {
                if (pm.Annotations[i] is RectangleAnnotation ra && ra.Tag as string == "phase-band")
                    pm.Annotations.RemoveAt(i);
            }

            foreach (var (fromX, toX, color, label) in bands)
            {
                pm.Annotations.Add(new RectangleAnnotation
                {
                    MinimumX = fromX,
                    MaximumX = toX,
                    Fill = WithAlpha(color, 72),
                    Layer = AnnotationLayer.BelowSeries,
                    Text = label ?? string.Empty,
                    TextColor = OxyColors.White,
                    TextHorizontalAlignment = HorizontalAlignment.Center,
                    TextVerticalAlignment = VerticalAlignment.Top,
                    Tag = "phase-band"
                });
            }

            pm.InvalidatePlot(false);
        }

        // Cursor API ------------------------------------------------------------

        // Move cursors using a frame index (e.g., from video). Wraps to chart range.
        public void SetCursorByFrame(int frameIndex)
        {
            if (frameIndex < 0)
            {
                HideCursor();
                return;
            }

            var x = ((frameIndex % MaxPoints) + MaxPoints) % MaxPoints; // safe modulo
            UpdateCursorAtX(x);
        }

        // Move cursors using time and fps.
        public void SetCursorByTime(TimeSpan position, double fps)
        {
            if (fps <= 0) throw new ArgumentOutOfRangeException(nameof(fps));
            if (position < TimeSpan.Zero)
            {
                HideCursor();
                return;
            }

            var frame = (int)Math.Round(position.TotalSeconds * fps);
            SetCursorByFrame(frame);
        }

        // Hide all cursor points.
        public void HideCursor()
        {
            SetPointXY(_cursorPoint1, double.NaN, double.NaN);
            SetPointXY(_cursorPoint2, double.NaN, double.NaN);
            SetPointXY(_cursorPoint3, double.NaN, double.NaN);

            PlotValue1.InvalidatePlot(false);
            PlotValue2.InvalidatePlot(false);
            PlotValue3.InvalidatePlot(false);
        }

        private void UpdateCursorAtX(int x)
        {
            // Clamp to axis range
            if (x < 0) x = 0;
            if (x > MaxPoints - 1) x = MaxPoints - 1;

            // Position value points at latest data sample for that X
            if (TryGetLatestYForX(_series1, x, out var y1))
                SetPointXY(_cursorPoint1, x, y1);
            else
                SetPointXY(_cursorPoint1, double.NaN, double.NaN);

            if (TryGetLatestYForX(_series2, x, out var y2))
                SetPointXY(_cursorPoint2, x, y2);
            else
                SetPointXY(_cursorPoint2, double.NaN, double.NaN);

            if (TryGetLatestYForX(_series3, x, out var y3))
                SetPointXY(_cursorPoint3, x, y3);
            else
                SetPointXY(_cursorPoint3, double.NaN, double.NaN);

            PlotValue1.InvalidatePlot(false);
            PlotValue2.InvalidatePlot(false);
            PlotValue3.InvalidatePlot(false);
        }

        private static bool TryGetLatestYForX(TwoColorAreaSeries series, int x, out double y)
        {
            var pts = series.Points;
            for (int i = pts.Count - 1; i >= 0; i--)
            {
                var p = pts[i];
                // AddData uses integer X, so compare as int
                if ((int)p.X == x)
                {
                    y = p.Y;
                    return true;
                }
            }
            y = 0;
            return false;
        }

        private static PointAnnotation CreateCursorPoint(OxyColor color)
        {
            return new PointAnnotation
            {
                X = double.NaN, // hidden until positioned
                Y = double.NaN,
                Shape = MarkerType.Circle,
                Size = 4,
                Fill = color,
                Stroke = OxyColors.White,
                StrokeThickness = 1.0,
                Layer = AnnotationLayer.AboveSeries,
                Text = string.Empty
            };
        }

        private static void SetPointXY(PointAnnotation point, double x, double y)
        {
            point.X = x;
            point.Y = y;
        }

        private static OxyColor WithAlpha(OxyColor c, byte a) => OxyColor.FromAColor(a, c);
    }
}