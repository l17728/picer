using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Picer.App.Views;

namespace Picer.E2E;

public sealed class SceneDescriptionEditorTests
{
    [Fact]
    public void FailedSavePreservesInputAndAllowsRetry()
    {
        Exception? failure=null;
        var thread=new Thread(()=>
        {
            var dispatcher=Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var window=new EditSceneDescriptionWindow("海边旅行","保留手工输入");
            var attempts=0;
            window.SaveAsync=async()=>
            {
                await Task.Yield();
                if(++attempts==1)throw new InvalidOperationException("服务暂时不可用");
            };
            window.Loaded+=async(_,_)=>
            {
                try
                {
                    var button=(Button)window.FindName("SaveButton");
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    for(var i=0;i<200 && !window.IsEnabled;i++)await Task.Delay(10);
                    Assert.True(window.IsEnabled);Assert.True(window.IsVisible);
                    Assert.Equal("海边旅行",window.SceneTitle);Assert.Equal("保留手工输入",window.SceneSummary);
                    Assert.Contains("输入内容已保留",((TextBlock)window.FindName("SaveError")).Text);
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                catch(Exception exception){failure=exception;window.Close();}
            };
            try { Assert.True(window.ShowDialog());Assert.Equal(2,attempts); }
            catch(Exception exception){failure=exception;}
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)),"场景编辑窗口测试超时");Assert.Null(failure);
    }
}
