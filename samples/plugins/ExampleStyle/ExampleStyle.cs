using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using AST;

namespace ExampleStyle
{
    /// <summary>A bar written entirely in code: one coloured block per open window, with no text.</summary>
    public sealed class BlocksStyle : IAstStyle
    {
        public string Id => "example.blocks";
        public string Name => "Blocks (example plugin)";

        public FrameworkElement CreateBar(BarViewModel bar)
        {
            var strip = new UniformGrid();
            void Rebuild()
            {
                strip.Children.Clear();
                strip.Rows = bar.IsVertical ? 0 : 1;
                strip.Columns = bar.IsVertical ? 1 : 0;
                foreach (var item in bar.Items) strip.Children.Add(Block(item));
            }

            ((INotifyCollectionChanged)bar.Items).CollectionChanged += (s, e) => Rebuild();
            bar.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(BarViewModel.IsVertical)) Rebuild();
            };
            Rebuild();
            return new Border { Background = new SolidColorBrush(Color.FromRgb(20, 12, 40)), Child = strip };
        }

        private static Button Block(TaskItem item)
        {
            var icon = new Image { Source = item.Icon, Width = 20, Height = 20 };
            var block = new Button
            {
                DataContext = item,
                Command = item.ActivateCommand,
                ToolTip = item.Title,
                Margin = new Thickness(2),
                Focusable = false,
                Background = Brush(item),
                Content = icon,
            };
            // A new item's icon arrives shortly after the item does.
            item.PropertyChanged += (s, e) =>
            {
                block.Background = Brush(item);
                icon.Source = item.Icon;
            };
            return block;
        }

        private static Brush Brush(TaskItem item) =>
            new SolidColorBrush(item.IsActive ? Color.FromRgb(255, 64, 160) : Color.FromRgb(70, 50, 120));
    }
}
