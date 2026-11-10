using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;
using OxyPlot;

namespace GuidingAnalyzer
{
    // N.I.N.A.'s docking templates can temporarily create more than one view of
    // the same VM. OxyPlot models must belong to one PlotView only, so each
    // binding generates its own presentation using the shared analysis data.
    public sealed class PlotForViewExtension : MarkupExtension
    {
        public PlotForViewExtension(string propertyName) { PropertyName = propertyName; }
        public string PropertyName { get; }

        public override object ProvideValue(IServiceProvider serviceProvider)
        {
            var binding = new MultiBinding {
                Mode = BindingMode.OneWay, Converter = new ViewPlotConverter(), ConverterParameter = PropertyName
            };
            binding.Bindings.Add(new Binding("DataContext." + PropertyName) {
                RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(GuidingAnalyzerView), 1)
            });
            binding.Bindings.Add(new Binding("DataContext") {
                RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(GuidingAnalyzerView), 1)
            });
            return binding.ProvideValue(serviceProvider);
        }

        private sealed class ViewPlotConverter : IMultiValueConverter
        {
            public object? Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
                values.Length == 2 && values[0] is PlotModel source && values[1] is GuidingAnalyzerVM vm
                    ? vm.CreatePlotForView((string)parameter, source) : null;

            public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
                Array.ConvertAll(targetTypes, _ => Binding.DoNothing);
        }
    }
}
