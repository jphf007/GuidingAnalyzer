using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Media;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.ViewModel;

namespace GuidingAnalyzer
{
    [Export(typeof(IDockableVM))]
    public class GuidingAnalyzerDockableVM : DockableVM
    {
        private readonly GuidingAnalyzerVM _vm;

        // ContentId must match the DataTemplate key suffix defined in GuidingAnalyzerDockableView.xaml.
        // The full key is: "GuidingAnalyzer.GuidingAnalyzerDockableVM_Dockable"
        public override string ContentId => "GuidingAnalyzerPanel";
        public override bool IsTool => true;

        [ImportingConstructor]
        public GuidingAnalyzerDockableVM(IProfileService profileService)
            : base(profileService)
        {
            Title = "Guiding Analyzer";
            _vm = new GuidingAnalyzerVM();
            IsVisible = true;

            // Icon: simple white sinusoid
            ImageGeometry = BuildSinusoidGeometry();
        }

        private static GeometryGroup BuildSinusoidGeometry()
        {
            // Single sinusoid — one full period, vertically centred in 24×24
            // Cubic Bezier: each half-period = 8 units, amplitude = ±9 (centre at y=12)
            var figure = new PathFigure { StartPoint = new Point(0, 12), IsClosed = false };

            // Rising: 0→8  (crest at y=3)
            figure.Segments.Add(new BezierSegment(
                new Point(2, 12), new Point(6,  3), new Point(8,  3), true));
            // Falling: 8→16  (trough at y=21)
            figure.Segments.Add(new BezierSegment(
                new Point(10,  3), new Point(14, 21), new Point(16, 21), true));
            // Rising back: 16→24  (return to zero)
            figure.Segments.Add(new BezierSegment(
                new Point(18, 21), new Point(22, 12), new Point(24, 12), true));

            var group = new GeometryGroup();
            group.Children.Add(new PathGeometry(new[] { figure }));
            group.Freeze();
            return group;
        }

        public GuidingAnalyzerVM AnalyzerVM => _vm;
    }
}
