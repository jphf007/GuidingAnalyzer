using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using OxyPlot;
using OxyPlot.Annotations;
using OxyPlot.Axes;
using OxyPlot.Wpf;

// Alias explicites pour lever les ambiguïtés OxyPlot.Xxx vs OxyPlot.Wpf.Xxx
using OxyAxis          = OxyPlot.Axes.Axis;
using OxyLineAnnotation = OxyPlot.Annotations.LineAnnotation;
using OxyPlotCommands  = OxyPlot.PlotCommands;

namespace GuidingAnalyzer
{
    /// <summary>
    /// Returns True if the bound string contains the parameter (ConverterParameter).
    /// Used for DataTrigger colouring of statistical metrics.
    /// </summary>
    public class StringContainsConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string s && parameter is string p)
                return s.Contains(p, StringComparison.OrdinalIgnoreCase);
            return false;
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// Inverse of a BooleanToVisibilityConverter:
    /// True → Collapsed, False → Visible.
    /// </summary>
    public class InverseBoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is bool b && b ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => value is Visibility v && v == Visibility.Collapsed;
    }
    [SupportedOSPlatform("windows7.0")]
    public partial class GuidingAnalyzerView : UserControl
    {
        private static readonly PlotController _plotController = BuildController();
        private static readonly PlotController _fftController  = BuildFftController();

        private static PlotController BuildController()
        {
            var c = new PlotController();
            c.UnbindAll();
            c.BindMouseDown(OxyMouseButton.Left, OxyPlotCommands.PanAt);
            c.BindMouseDown(OxyMouseButton.Left, OxyModifierKeys.None, 2,
                new DelegatePlotCommand<OxyMouseDownEventArgs>(
                    (view, ctrl, args) => view.ActualModel?.ResetAllAxes()));
            c.BindMouseWheel(OxyPlotCommands.ZoomWheel);
            return c;
        }

        private static PlotController BuildFftController()
        {
            var c = new PlotController();
            c.UnbindAll();
            c.BindMouseDown(OxyMouseButton.Left, OxyPlotCommands.PanAt);
            c.BindMouseDown(OxyMouseButton.Left, OxyModifierKeys.None, 2,
                new DelegatePlotCommand<OxyMouseDownEventArgs>(
                    (view, ctrl, args) => view.ActualModel?.ResetAllAxes()));
            c.BindMouseWheel(
                new DelegatePlotCommand<OxyMouseWheelEventArgs>((view, ctrl, args) =>
                {
                    var model = view.ActualModel;
                    if (model == null) return;
                    foreach (var axis in model.Axes)
                        if (axis.Position == AxisPosition.Bottom || axis.Position == AxisPosition.Top)
                        {
                            double factor = args.Delta > 0 ? 0.8 : 1.25;
                            axis.ZoomAt(factor, axis.InverseTransform(args.Position.X));
                        }
                    model.InvalidatePlot(false);
                }));
            return c;
        }

        public GuidingAnalyzerView()
        {
            InitializeComponent();
            Loaded += (_, _) => ApplyController();
        }

        private void ApplyController()
        {
            GuidingPlot.Controller          = _plotController;
            MountPlot.Controller            = _plotController;
            FftRaPlotView.Controller        = _fftController;
            FftDecPlotView.Controller       = _fftController;
            FftRaUncorrPlotView.Controller  = _fftController;
            FftDecUncorrPlotView.Controller = _fftController;
            CalibrationPlotView.Controller  = _plotController;
            RmsVsAltitudePlotView.Controller = _plotController;
            SlidingRmsPlotView.Controller    = _plotController;

            // Clean up crosshair on zoom/pan (ModelChanged → subscribe AxisChanged)
            HookAxisChanged(GuidingPlot);
            HookAxisChanged(MountPlot);
            HookAxisChanged(RmsVsAltitudePlotView);
            HookAxisChanged(SlidingRmsPlotView);
        }

        private static void HookAxisChanged(OxyPlot.Wpf.PlotView plotView)
        {
            // Subscribe each time the model changes (new session loaded)
            plotView.DataContextChanged += (_, _) => SubscribeAxes(plotView);
            // And also at startup if the model is already present
            SubscribeAxes(plotView);
        }

        private static void SubscribeAxes(OxyPlot.Wpf.PlotView plotView)
        {
            var model = plotView.ActualModel;
            if (model == null) return;
#pragma warning disable CS0618
            foreach (var ax in model.Axes)
                ax.AxisChanged += (_, _) => ClearCrosshair(model);
#pragma warning restore CS0618
        }

        // ── Crosshair rouge via WPF MouseMove natif ───────────────────────────
        private void PlotView_MouseMove(object sender, MouseEventArgs e)
        {
            if (sender is not PlotView plotView) return;
            var model = plotView.ActualModel;
            if (model == null) return;

            var wpfPos = e.GetPosition(plotView);
            var pos    = new ScreenPoint(wpfPos.X, wpfPos.Y);

            OxyAxis? xAxis = null, yAxis = null;
            foreach (var ax in model.Axes)
            {
                if (ax is OxyAxis oa)
                {
                    if (oa.Position == AxisPosition.Bottom) xAxis = oa;
                    if (oa.Position == AxisPosition.Left)   yAxis = oa;
                }
            }
            if (xAxis is null || yAxis is null) return;

            var plotArea = model.PlotArea;
            if (pos.X < plotArea.Left || pos.X > plotArea.Right ||
                pos.Y < plotArea.Top  || pos.Y > plotArea.Bottom)
            {
                ClearCrosshair(model);
                return;
            }

            var dp = xAxis.InverseTransform(pos.X, pos.Y, yAxis);
            double dataX = dp.X;
            double dataY = dp.Y;

            // Remove old crosshairs (lines + text)
            var toRemove = model.Annotations
                .Where(a => (a.Tag as string) == "crosshair")
                .ToList();
            foreach (var a in toRemove) model.Annotations.Remove(a);

            var red = OxyColor.FromAColor(220, OxyColors.Red);

            // Y axis label: RMS in arcsec for all charts
            string yLabel = $"{dataY:F3}\"";

            // X axis label: adapt according to the chart
            string xTitle = xAxis.Title ?? "";
            string xLabel = xTitle.Contains("Altitude")  ? $"{dataX:F1}°"
                          : xTitle.Contains("min")        ? $"{dataX:F0} min"
                          : xTitle.Contains("frame") || xTitle.Contains("Frame") ? $"#{dataX:F0}"
                          : $"{dataX:F1}";

            model.Annotations.Add(new OxyLineAnnotation
            {
                Tag                     = "crosshair",
                Type                    = LineAnnotationType.Horizontal,
                Y                       = dataY,
                Color                   = red,
                LineStyle               = LineStyle.Solid,
                StrokeThickness         = 1,
                Text                    = yLabel,
                TextColor               = OxyColors.Red,
                TextHorizontalAlignment = OxyPlot.HorizontalAlignment.Right,
                TextVerticalAlignment   = OxyPlot.VerticalAlignment.Bottom,
                FontSize                = 10,
                FontWeight              = OxyPlot.FontWeights.Bold,
            });

            model.Annotations.Add(new OxyLineAnnotation
            {
                Tag                     = "crosshair",
                Type                    = LineAnnotationType.Vertical,
                X                       = dataX,
                Color                   = red,
                LineStyle               = LineStyle.Solid,
                StrokeThickness         = 1,
                Text                    = xLabel,
                TextColor               = OxyColors.Red,
                TextHorizontalAlignment = OxyPlot.HorizontalAlignment.Left,
                TextVerticalAlignment   = OxyPlot.VerticalAlignment.Top,
                TextLinePosition        = 0.02,
                FontSize                = 10,
                FontWeight              = OxyPlot.FontWeights.Bold,
            });

            model.InvalidatePlot(false);
        }

        private void PlotView_MouseLeave(object sender, MouseEventArgs e)
        {
            if (sender is not PlotView plotView) return;
            var model = plotView.ActualModel;
            if (model != null) ClearCrosshair(model);
        }

        private static void ClearCrosshair(PlotModel model)
        {
            var toRemove = model.Annotations
                .Where(a => (a.Tag as string) == "crosshair")
                .ToList();
            if (toRemove.Count > 0)
            {
                foreach (var a in toRemove) model.Annotations.Remove(a);
                model.InvalidatePlot(false);
            }
        }

        // ── Reset zoom ────────────────────────────────────────────────────────
        private static void ResetZoom(PlotView? plot)
        {
            if (plot?.Model == null) return;
            plot.Model.ResetAllAxes();
            plot.InvalidatePlot(false);
        }

        /// <summary>
        /// Remet le zoom en centrant l'axe Y symétriquement autour de 0 :
        /// le zéro est au milieu de la zone de tracé dans les deux graphiques
        /// de l'onglet "Courbes PHD2" (erreurs brutes et courbe reconstituée).
        /// </summary>
        private static void ResetZoomCentered(PlotView? plot)
        {
            if (plot?.Model == null) return;
            var model = plot.Model;

            // Collect all Y values from line series
            double maxAbs = 0;
            foreach (var series in model.Series)
            {
                if (series is OxyPlot.Series.LineSeries ls)
                {
                    foreach (var pt in ls.Points)
                    {
                        if (!double.IsNaN(pt.Y))
                            maxAbs = System.Math.Max(maxAbs, System.Math.Abs(pt.Y));
                    }
                }
            }

            // Reset X axis normally
            foreach (var ax in model.Axes)
                if (ax.Position == AxisPosition.Bottom || ax.Position == AxisPosition.Top)
                    ax.Reset();

            // Symmetric Y axis : [-maxAbs*1.10 ; +maxAbs*1.10]
            double margin = maxAbs > 0 ? maxAbs * 1.10 : 1.0;
            foreach (var ax in model.Axes)
            {
                if (ax.Position == AxisPosition.Left || ax.Position == AxisPosition.Right)
                {
                    ax.Zoom(-margin, margin);
                }
            }

            model.InvalidatePlot(false);
        }

        private void ResetGuidingZoom_Click(object s, RoutedEventArgs e)      => ResetZoomCentered(GuidingPlot);
        private void ResetMountZoom_Click(object s, RoutedEventArgs e)        => ResetZoomCentered(MountPlot);
        private void ResetFftRaZoom_Click(object s, RoutedEventArgs e)        => ResetZoom(FftRaPlotView);
        private void ResetFftDecZoom_Click(object s, RoutedEventArgs e)       => ResetZoom(FftDecPlotView);
        private void ResetFftRaUncorrZoom_Click(object s, RoutedEventArgs e)  => ResetZoom(FftRaUncorrPlotView);
        private void ResetFftDecUncorrZoom_Click(object s, RoutedEventArgs e) => ResetZoom(FftDecUncorrPlotView);
        private void ResetCalibrationZoom_Click(object s, RoutedEventArgs e)  => ResetZoom(CalibrationPlotView);

        private void RefreshPolarAlignment_Click(object s, RoutedEventArgs e)
        {
            if (DataContext is GuidingAnalyzerVM vm)
                vm.RefreshPolarAlignmentCommand.Execute(null);
        }
    }
}
