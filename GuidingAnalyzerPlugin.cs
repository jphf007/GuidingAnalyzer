using System.ComponentModel.Composition;
using NINA.Plugin;
using NINA.Plugin.Interfaces;

namespace GuidingAnalyzer
{
    [Export(typeof(IPluginManifest))]
    public class GuidingAnalyzerPlugin : PluginBase
    {
        // PluginBase automatically reads all [AssemblyMetadata], [AssemblyTitle],
        // [Guid] etc. from AssemblyInfo.cs
        [ImportingConstructor]
        public GuidingAnalyzerPlugin()
        {
        }
    }
}
