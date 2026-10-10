using System.Reflection;
using Picer.Core.Gateway;
using Forms = System.Windows.Forms;

namespace Picer.E2E;

public sealed class MediaAgentFirstRunFormTests
{
    [Fact]
    public void StandaloneSettingsShowsChineseControlsRejectsBlankInputAndClosesWithoutSaving()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var saveCalls = 0;
                var type = Assembly.Load("Picer.MediaAgent").GetType("Picer.MediaAgent.AgentSettingsForm", throwOnError: true)!;
                Action<MediaAgentSettings> save = _ => saveCalls++;
                using var form = (Forms.Form)Activator.CreateInstance(type, [null, save])!;
                try
                {
                    form.Show();
                    Forms.Application.DoEvents();
                    Assert.True(form.Visible);
                    Assert.Equal("Picer 媒体代理设置", form.Text);
                    var controls = Descendants(form).ToArray();
                    Assert.All(controls.OfType<Forms.TextBox>(), control => Assert.True(control.Visible));
                    var address = Assert.Single(controls.OfType<Forms.TextBox>().Where(box => !box.UseSystemPasswordChar));
                    var token = Assert.Single(controls.OfType<Forms.TextBox>().Where(box => box.UseSystemPasswordChar));
                    Assert.Equal("", address.Text);
                    Assert.Equal("", token.Text);
                    Assert.Contains(controls.OfType<Forms.Label>(), label => label.Text == "索引服务地址");
                    Assert.Contains(controls.OfType<Forms.Label>(), label => label.Text == "允许服务读取的媒体目录及其所有子目录");
                    var roots = Assert.Single(controls.OfType<Forms.ListBox>());
                    Assert.True(roots.Visible);
                    Assert.Empty(roots.Items.Cast<string>());
                    Assert.Contains(controls.OfType<Forms.Button>(), button => button.Text == "添加目录" && button.Visible);
                    var remove = Assert.Single(controls.OfType<Forms.Button>().Where(button => button.Text == "移除目录"));
                    // Synthetic paths are only list entries; the picker, disk and network are never accessed.
                    roots.Items.AddRange([@"C:\synthetic-media-a", @"D:\synthetic-media-b"]);
                    Assert.Equal(2, roots.Items.Count);
                    roots.SelectedIndex = 0;
                    remove.PerformClick();
                    Assert.Single(roots.Items.Cast<string>());
                    roots.Items.Clear();
                    var pair = Assert.Single(controls.OfType<Forms.Button>().Where(button => button.Text == "配对并保存"));
                    pair.PerformClick();
                    Forms.Application.DoEvents();
                    Assert.Contains(controls.OfType<Forms.Label>(), label => label.Text.StartsWith("请输入 HTTPS", StringComparison.Ordinal));
                    Assert.True(pair.Enabled);
                    Assert.Equal(0, saveCalls);
                    Assert.Null(type.GetProperty("Settings")!.GetValue(form));
                    Assert.True(form.Visible);
                    form.Close();
                    Forms.Application.DoEvents();
                    Assert.False(form.Visible);
                    Assert.Equal(0, saveCalls);
                }
                finally { if (!form.IsDisposed) form.Close(); }
            }
            catch (Exception exception) { failure = exception; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "代理首次设置窗口测试超时。");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static IEnumerable<Forms.Control> Descendants(Forms.Control parent)
    {
        foreach (Forms.Control child in parent.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
