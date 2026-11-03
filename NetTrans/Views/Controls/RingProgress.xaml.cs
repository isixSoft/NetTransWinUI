using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace NetTrans.Views.Controls;

/// <summary>
/// The inspector's ring. SVG expresses the sweep as stroke-dasharray /
/// stroke-dashoffset; WinUI's dash values are multiples of the stroke width, so
/// the circumference is divided by the 7px stroke before being handed over.
/// </summary>
public sealed partial class RingProgress : UserControl
{
    private const double Radius = 34;
    private const double StrokeWidth = 7;
    private static readonly double DashUnits = 2 * Math.PI * Radius / StrokeWidth;

    public static readonly DependencyProperty DiameterProperty =
        DependencyProperty.Register(nameof(Diameter), typeof(double), typeof(RingProgress), new PropertyMetadata(98d));

    public static readonly DependencyProperty FractionProperty =
        DependencyProperty.Register(nameof(Fraction), typeof(double), typeof(RingProgress),
            new PropertyMetadata(0d, (d, _) => ((RingProgress)d).Apply()));

    public static readonly DependencyProperty ArcBrushProperty =
        DependencyProperty.Register(nameof(ArcBrush), typeof(Brush), typeof(RingProgress),
            new PropertyMetadata(null, (d, _) => ((RingProgress)d).Apply()));

    public static readonly DependencyProperty PercentProperty =
        DependencyProperty.Register(nameof(Percent), typeof(string), typeof(RingProgress),
            new PropertyMetadata("0", (d, _) => ((RingProgress)d).Apply()));

    public static readonly DependencyProperty SubtitleProperty =
        DependencyProperty.Register(nameof(Subtitle), typeof(string), typeof(RingProgress),
            new PropertyMetadata("", (d, _) => ((RingProgress)d).Apply()));

    private bool _placed;

    public double Diameter
    {
        get => (double)GetValue(DiameterProperty);
        set => SetValue(DiameterProperty, value);
    }

    public double Fraction
    {
        get => (double)GetValue(FractionProperty);
        set => SetValue(FractionProperty, value);
    }

    public Brush? ArcBrush
    {
        get => (Brush?)GetValue(ArcBrushProperty);
        set => SetValue(ArcBrushProperty, value);
    }

    public string Percent
    {
        get => (string)GetValue(PercentProperty);
        set => SetValue(PercentProperty, value);
    }

    public string Subtitle
    {
        get => (string)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    public RingProgress()
    {
        InitializeComponent();
        Arc.StrokeDashArray = new DoubleCollection { DashUnits, DashUnits };
        Loaded += (_, _) => Apply();
    }

    private void Apply()
    {
        PercentText.Text = Percent;
        SubText.Text = Subtitle;
        if (ArcBrush is not null) Arc.Stroke = ArcBrush;

        double target = DashUnits * (1 - Math.Clamp(Fraction, 0, 1));

        // The first placement snaps; later updates ride the CSS transition.
        if (!_placed || !IsLoaded)
        {
            Arc.StrokeDashOffset = target;
            _placed = true;
            return;
        }

        // CSS: transition: stroke-dashoffset .6s linear.
        var frames = new DoubleAnimationUsingKeyFrames { EnableDependentAnimation = true };
        frames.KeyFrames.Add(new LinearDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(600)),
            Value = target,
        });

        Storyboard.SetTarget(frames, Arc);
        Storyboard.SetTargetProperty(frames, "StrokeDashOffset");

        var storyboard = new Storyboard();
        storyboard.Children.Add(frames);
        storyboard.Begin();
    }
}
