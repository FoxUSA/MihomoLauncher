using System;
using System.Windows;
using System.Windows.Controls;

namespace MihomoLauncher.Views
{
    // A grid of equal cells filled column by column (2026-11-27, the rule editor's VPN list:
    // «считаем 1 2 3 по столбикам — первому, потом на второй столбик 4 5 6»). With Columns = 2 and
    // six items: 1 2 3 down the left column, 4 5 6 down the right. Rows = ceil(items / Columns);
    // every cell is the panel's width / Columns wide and as tall as the tallest item.
    public sealed class ColumnFirstGrid : Panel
    {
        public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(
            nameof(Columns), typeof(int), typeof(ColumnFirstGrid),
            new FrameworkPropertyMetadata(2, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public int Columns
        {
            get => (int)GetValue(ColumnsProperty);
            set => SetValue(ColumnsProperty, value);
        }

        private int Cols => Math.Max(1, Columns);
        private int Rows(int n) => Math.Max(1, (n + Cols - 1) / Cols);

        protected override Size MeasureOverride(Size available)
        {
            int n = InternalChildren.Count;
            double cellW = double.IsInfinity(available.Width) ? double.PositiveInfinity : available.Width / Cols;
            double maxW = 0, maxH = 0;
            foreach (UIElement c in InternalChildren)
            {
                c.Measure(new Size(cellW, double.PositiveInfinity));
                maxW = Math.Max(maxW, c.DesiredSize.Width);
                maxH = Math.Max(maxH, c.DesiredSize.Height);
            }
            if (n == 0) return new Size(0, 0);
            double w = double.IsInfinity(available.Width) ? maxW * Cols : available.Width;
            return new Size(w, maxH * Rows(n));
        }

        protected override Size ArrangeOverride(Size final)
        {
            int n = InternalChildren.Count;
            if (n == 0) return final;
            int rows = Rows(n);
            double cellW = final.Width / Cols;
            double cellH = 0;
            foreach (UIElement c in InternalChildren) cellH = Math.Max(cellH, c.DesiredSize.Height);
            for (int i = 0; i < n; i++)
            {
                int col = i / rows, row = i % rows;
                InternalChildren[i].Arrange(new Rect(col * cellW, row * cellH, cellW, cellH));
            }
            return final;
        }
    }
}
