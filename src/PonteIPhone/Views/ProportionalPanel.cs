using System.Windows;
using System.Windows.Controls;

namespace PonteIPhone.Views;

/// <summary>
/// Mette i figli in fila orizzontale, ognuno largo in proporzione al suo <see cref="WeightProperty"/>:
/// la barra della memoria come quella di iTunes (App, Foto, Musica… Libero).
/// </summary>
public sealed class ProportionalPanel : Panel
{
    public static readonly DependencyProperty WeightProperty = DependencyProperty.RegisterAttached(
        "Weight", typeof(double), typeof(ProportionalPanel),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsParentArrange | FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    public static double GetWeight(DependencyObject o) => (double)o.GetValue(WeightProperty);
    public static void SetWeight(DependencyObject o, double value) => o.SetValue(WeightProperty, value);

    protected override Size MeasureOverride(Size available)
    {
        double height = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(double.PositiveInfinity, available.Height));
            height = Math.Max(height, child.DesiredSize.Height);
        }
        return new Size(double.IsInfinity(available.Width) ? 0 : available.Width, height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        double total = 0;
        foreach (UIElement child in InternalChildren) total += Math.Max(0, GetWeight(child));
        double x = 0;
        foreach (UIElement child in InternalChildren)
        {
            double w = total > 0 ? final.Width * Math.Max(0, GetWeight(child)) / total : 0;
            child.Arrange(new Rect(x, 0, w, final.Height));
            x += w;
        }
        return final;
    }
}
