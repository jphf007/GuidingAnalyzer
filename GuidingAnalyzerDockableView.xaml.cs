using System.ComponentModel.Composition;
using System.IO;
using System.Windows;

namespace GuidingAnalyzer
{
    [Export(typeof(ResourceDictionary))]
    public partial class GuidingAnalyzerDockableView : ResourceDictionary
    {
        public GuidingAnalyzerDockableView()
        {
            // Debug log to confirm MEF is instantiating this ResourceDictionary
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "GuidingAnalyzer_debug.txt"),
                $"{System.DateTime.Now}: GuidingAnalyzerDockableView constructor called\n"
            );

            InitializeComponent();

            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "GuidingAnalyzer_debug.txt"),
                $"{System.DateTime.Now}: InitializeComponent OK - {this.Count} entries in dictionary\n"
            );
        }
    }
}
