using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Picer.App;
using Picer.App.Views;

namespace Picer.E2E;

public sealed class ClassificationPairingRecoveryTests
{
    [Fact]
    public void MissingPairingAfterRemoteStartupOpensAccountDialogInsteadOfLosingMenuClick()
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-pairing-menu-" + Guid.NewGuid().ToString("N"));
        var previous = Environment.GetEnvironmentVariable("PICER_BOOTSTRAP_ROOT");
        var mode = typeof(AppServices).GetProperty(nameof(AppServices.ServiceMode))!;
        var previousMode = AppServices.ServiceMode;
        Exception? failure = null;
        var opened = false;
        var thread = new Thread(() =>
        {
            MainWindow? main = null;
            try
            {
                Environment.SetEnvironmentVariable("PICER_BOOTSTRAP_ROOT", root);
                mode.SetValue(null, true);
                main = new MainWindow();
                // Reproduce an already loaded remote shell after its saved pairing was forgotten.
                typeof(MainWindow).GetField("_initialized", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(main, true);
                main.Show();
                var owner = main;
                Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() =>
                {
                    var dialog = owner.OwnedWindows.OfType<AccountConnectionWindow>().SingleOrDefault();
                    opened = dialog is not null && ((Button)dialog.FindName("LoginButton")).IsEnabled;
                    dialog?.Close();
                }), DispatcherPriority.Background);
                var menu = ((DockPanel)main.Content).Children.OfType<Menu>().Single();
                var classification = menu.Items.OfType<MenuItem>().SelectMany(item => item.Items.OfType<MenuItem>())
                    .Single(item => System.Windows.Automation.AutomationProperties.GetAutomationId(item) == "PhotoClassificationMenu");
                classification.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            }
            catch (Exception exception) { failure = exception; }
            finally
            {
                main?.Close();
                mode.SetValue(null, previousMode);
                Environment.SetEnvironmentVariable("PICER_BOOTSTRAP_ROOT", previous);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(40)), "分类重新配对入口未返回");
        Assert.Null(failure);
        Assert.True(opened, "应打开可继续登录的设备配对窗口");
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
