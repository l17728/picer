using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Picer.App.Views;

namespace Picer.E2E;

public sealed class PhotoViewerTests
{
    [Theory]
    [InlineData(4000, 3000)]
    [InlineData(2000, 5000)]
    public void LargePhotoFitsCompletelyAndRefitsWhenWindowChanges(int width, int height)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            PhotoViewerWindow? window = null;
            try
            {
                var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Gray8, null);
                window = new PhotoViewerWindow(bitmap, "预览测试"); window.Show();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                var panel = Assert.IsType<DockPanel>(window.Content);
                var scroll = Assert.IsType<ScrollViewer>(panel.Children[1]);
                var image = Assert.IsType<Image>(scroll.Content);
                Check(); window.Width = 700; window.Height = 550;
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); Check();
                void Check()
                {
                    Assert.True(image.ActualWidth <= scroll.ActualWidth);
                    Assert.True(image.ActualHeight <= scroll.ActualHeight);
                    Assert.Equal((double)width / height, image.ActualWidth / image.ActualHeight, 4);
                    Assert.InRange(scroll.ScrollableWidth, 0, 1);
                    Assert.InRange(scroll.ScrollableHeight, 0, 1);
                }
            }
            catch(Exception exception) { failure = exception; }
            finally { window?.Close(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
