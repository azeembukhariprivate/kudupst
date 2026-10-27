using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Kudupst.Controls
{
    /// <summary>The KUDUPST mark: an envelope with a magnifying glass. Uses the text colour.</summary>
    public class LogoMark : Viewbox
    {
        public LogoMark(double size = 40, Brush lensFill = null)
        {
            Width = size;
            Height = size;
            Focusable = false;
            IsHitTestVisible = false;

            var canvas = new Canvas { Width = 64, Height = 64 };
            var envelope = new Rectangle { Width = 42, Height = 30, RadiusX = 4, RadiusY = 4, StrokeThickness = 3.5 };
            Canvas.SetLeft(envelope, 7);
            Canvas.SetTop(envelope, 15);
            Bind(envelope, Shape.StrokeProperty);
            canvas.Children.Add(envelope);

            var flap = new Polyline { Points = new PointCollection { new Point(9, 18), new Point(28, 32), new Point(47, 18) }, StrokeThickness = 3.5, StrokeLineJoin = PenLineJoin.Round };
            Bind(flap, Shape.StrokeProperty);
            canvas.Children.Add(flap);

            var lens = new Ellipse { Width = 20, Height = 20, StrokeThickness = 3.5, Fill = lensFill ?? Brushes.Transparent };
            Canvas.SetLeft(lens, 35);
            Canvas.SetTop(lens, 33);
            Bind(lens, Shape.StrokeProperty);
            canvas.Children.Add(lens);

            var handle = new Line { X1 = 52.5, Y1 = 50.5, X2 = 58.5, Y2 = 56.5, StrokeThickness = 4, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            Bind(handle, Shape.StrokeProperty);
            canvas.Children.Add(handle);
            Child = canvas;
        }

        private void Bind(Shape shape, DependencyProperty property) =>
            shape.SetBinding(property, new Binding { Path = new PropertyPath(TextElement.ForegroundProperty), Source = this });
    }

    /// <summary>The app tile: the mark in white on an indigo rounded square.</summary>
    public static class LogoTile
    {
        public static readonly Color Indigo = Color.FromRgb(0x4F, 0x46, 0xE5);

        public static Border Create(double size)
        {
            var fill = new SolidColorBrush(Indigo);
            var mark = new LogoMark(size, fill);
            mark.SetValue(TextElement.ForegroundProperty, Brushes.White);
            return new Border
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(size * 0.234),
                Background = fill,
                Child = mark
            };
        }
    }
}
