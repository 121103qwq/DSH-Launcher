using System.Net;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml.Linq;
using DshLauncher.Models;
using DshLauncher.Services;
using Xunit;

namespace DshLauncher.UnitTests;

[Collection("WpfRendering")]
public sealed class ModPackMarketViewTests
{
    [Fact]
    public void LeavingPageCancelsLoadAndLateCatalogCannotPopulateIt()
    {
        var response = new TaskCompletionSource<HttpResponseMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new PlannedHttpHandler(_ => response.Task);
        using var client = new HttpClient(handler);

        WpfPageHost.Run(root =>
        {
            var page = CreatePage(client, root, (_, _) => Task.FromResult("installed"));
            WaitFor(() => handler.CallCount == 1);
            root.Children.Remove(page);
            page.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent, page));

            response.SetResult(CatalogResponse(Catalog("old-pack", "旧列表")));
            PumpDispatcher(TimeSpan.FromMilliseconds(80));

            var list = (ListBox)page.FindName("PackList")!;
            Assert.Empty(list.Items);
        });
    }

    [Fact]
    public void ReloadUsesNewRequestAndIgnoresLateResultFromPreviousPageLifetime()
    {
        var firstResponse = new TaskCompletionSource<HttpResponseMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new PlannedHttpHandler(
            _ => firstResponse.Task,
            _ => Task.FromResult(CatalogResponse(Catalog("new-pack", "新列表"))));
        using var client = new HttpClient(handler);

        WpfPageHost.Run(root =>
        {
            var page = CreatePage(client, root, (_, _) => Task.FromResult("installed"));
            WaitFor(() => handler.CallCount == 1);
            root.Children.Remove(page);
            page.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent, page));
            root.Children.Add(page);
            page.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, page));
            Layout(root);
            WaitFor(() => handler.CallCount == 2);

            var list = (ListBox)page.FindName("PackList")!;
            WaitFor(() => list.Items.Count == 1);
            var current = Assert.IsType<ModPackMarketEntry>(list.Items[0]);
            Assert.Equal("new-pack", current.Name);

            firstResponse.SetResult(CatalogResponse(Catalog("old-pack", "旧列表")));
            PumpDispatcher(TimeSpan.FromMilliseconds(80));

            current = Assert.IsType<ModPackMarketEntry>(list.Items[0]);
            Assert.Equal("new-pack", current.Name);
        });
    }

    [Fact]
    public void SearchRemovesSelectionAndUnavailableEntryCannotBeInstalled()
    {
        var handler = new PlannedHttpHandler(_ => Task.FromResult(CatalogResponse(
            Catalog("safe-pack", "可安装", downloadUrl: "https://example.test/safe.tgz"),
            Catalog("blocked-pack", "不可安装", downloadUrl: ""))));
        using var client = new HttpClient(handler);
        var installCalls = 0;

        WpfPageHost.Run(root =>
        {
            var page = CreatePage(client, root, (_, _) =>
            {
                Interlocked.Increment(ref installCalls);
                return Task.FromResult("installed");
            });
            var list = (ListBox)page.FindName("PackList")!;
            var search = (TextBox)page.FindName("SearchBox")!;
            var install = (Button)page.FindName("InstallButton")!;

            WaitFor(() => list.Items.Count == 2);
            var blocked = Assert.IsType<ModPackMarketEntry>(list.Items.Cast<object>()
                .Single(item => ((ModPackMarketEntry)item).Name == "blocked-pack"));
            list.SelectedItem = blocked;
            Layout(root);
            Assert.False(install.IsEnabled);

            install.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(0, Volatile.Read(ref installCalls));

            search.Text = "safe-pack";
            WaitFor(() => list.Items.Count == 1);
            Assert.Null(list.SelectedItem);
            Assert.False(install.IsEnabled);
            Assert.Equal("safe-pack", Assert.IsType<ModPackMarketEntry>(list.Items[0]).Name);
        });
    }

    [Fact]
    public void InstallClickStartsOnlyOneOperationWhileTheFirstIsInFlight()
    {
        var handler = new PlannedHttpHandler(_ => Task.FromResult(CatalogResponse(
            Catalog("safe-pack", "可安装", downloadUrl: "https://example.test/safe.tgz"))));
        using var client = new HttpClient(handler);
        var installStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var installComplete = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var installCalls = 0;

        WpfPageHost.Run(root =>
        {
            var page = CreatePage(client, root, (_, _) =>
            {
                Interlocked.Increment(ref installCalls);
                installStarted.SetResult(true);
                return installComplete.Task;
            });
            var list = (ListBox)page.FindName("PackList")!;
            var install = (Button)page.FindName("InstallButton")!;

            WaitFor(() => list.Items.Count == 1);
            list.SelectedIndex = 0;
            Layout(root);
            Assert.True(install.IsEnabled);

            install.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitFor(() => installStarted.Task.IsCompleted);
            install.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(1, Volatile.Read(ref installCalls));
            Assert.False(install.IsEnabled);

            installComplete.SetResult("安装完成");
            WaitFor(() => install.IsEnabled);
        });
    }

    private static ModPackMarketView CreatePage(
        HttpClient client,
        Grid root,
        Func<ModPackMarketEntry, CancellationToken, Task<string>> install)
    {
        var page = new ModPackMarketView(CreateService(client), install);
        root.Children.Add(page);
        Layout(root);
        if (!page.IsLoaded)
        {
            page.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, page));
        }

        return page;
    }

    private static ModPackMarketService CreateService(HttpClient client) => new(client);

    private static object Catalog(
        string name,
        string description,
        string downloadUrl = "https://example.test/pack.tgz") => new
    {
        name,
        displayName = name,
        version = "1.0.0",
        description,
        author = "测试作者",
        icon = "",
        dshVersion = ">=0.1.0",
        bundles = Array.Empty<string>(),
        dependencies = new { },
        category = "coding",
        downloadUrl,
        sha256 = new string('a', 64),
        size = 1024,
        updatedAt = "2026-09-13"
    };

    private static HttpResponseMessage CatalogResponse(params object[] entries)
    {
        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            generatedAt = "2026-09-13T00:00:00Z",
            modpacks = entries
        });
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json")
        };
    }

    private static void WaitFor(Func<bool> condition, int timeoutMilliseconds = 2000)
    {
        if (condition()) return;
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(10)
        };
        timer.Tick += (_, _) =>
        {
            if (condition())
            {
                timer.Stop();
                frame.Continue = false;
            }
        };
        timer.Start();
        var timeout = new DispatcherTimer(DispatcherPriority.ApplicationIdle)
        {
            Interval = TimeSpan.FromMilliseconds(timeoutMilliseconds)
        };
        timeout.Tick += (_, _) =>
        {
            timeout.Stop();
            timer.Stop();
            frame.Continue = false;
        };
        timeout.Start();
        Dispatcher.PushFrame(frame);
        Assert.True(condition(), "Timed out waiting for WPF state.");
    }

    private static void PumpDispatcher(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = duration
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void Layout(FrameworkElement element)
    {
        element.Measure(new Size(960, 620));
        element.Arrange(new Rect(0, 0, 960, 620));
        element.UpdateLayout();
    }

    private sealed class PlannedHttpHandler(
        params Func<CancellationToken, Task<HttpResponseMessage>>[] plans) : HttpMessageHandler
    {
        private readonly Queue<Func<CancellationToken, Task<HttpResponseMessage>>> _plans = new(plans);
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            Func<CancellationToken, Task<HttpResponseMessage>> plan;
            lock (_plans)
            {
                plan = _plans.Count > 0
                    ? _plans.Dequeue()
                    : _ => Task.FromResult(CatalogResponse());
            }

            return plan(cancellationToken);
        }
    }

    internal static class WpfPageHost
    {
        private static readonly object Gate = new();
        private static readonly TaskCompletionSource<Dispatcher> Ready = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private static Dispatcher? _dispatcher;
        private static Thread? _thread;

        public static void Run(Action<Grid> action)
        {
            EnsureStarted();
            var completed = new TaskCompletionSource<Exception?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _dispatcher!.BeginInvoke(DispatcherPriority.Send, new Action(() =>
            {
                try
                {
                    using var source = new HwndSource(new HwndSourceParameters("ModPack market tests")
                    {
                        WindowStyle = 0,
                        ExtendedWindowStyle = 0x08000080,
                        PositionX = -32000,
                        PositionY = -32000,
                        Width = 960,
                        Height = 620
                    });
                    var root = new Grid { Background = System.Windows.Media.Brushes.White };
                    source.RootVisual = root;
                    action(root);
                    completed.SetResult(null);
                }
                catch (Exception exception)
                {
                    completed.SetResult(exception);
                }
            }));

            var failure = completed.Task.GetAwaiter().GetResult();
            if (failure is not null)
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }
        }

        private static void EnsureStarted()
        {
            lock (Gate)
            {
                if (_thread is not null) return;
                _thread = new Thread(() =>
                {
                    try
                    {
                        var application = new Application
                        {
                            Resources = LoadResources()
                        };
                        _dispatcher = Dispatcher.CurrentDispatcher;
                        Ready.SetResult(_dispatcher);
                        Dispatcher.Run();
                        application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                    }
                    catch (Exception exception)
                    {
                        Ready.SetException(exception);
                    }
                })
                {
                    IsBackground = true
                };
                _thread.SetApartmentState(ApartmentState.STA);
                _thread.Start();
            }

            _dispatcher = Ready.Task.GetAwaiter().GetResult();
        }

        private static ResourceDictionary LoadResources()
        {
            XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
            var app = System.Xml.Linq.XDocument.Load(XamlTestResources.SourceFile("App.xaml"));
            var dictionary = new System.Xml.Linq.XElement(
                wpf + "ResourceDictionary",
                new System.Xml.Linq.XAttribute(System.Xml.Linq.XNamespace.Xmlns + "x", xaml.NamespaceName),
                app.Root!.Element(wpf + "Application.Resources")!.Elements()
                    .Select(element => new System.Xml.Linq.XElement(element)));
            XamlTestResources.NormalizeAssemblyNamespaces(dictionary);
            return (ResourceDictionary)XamlReader.Parse(dictionary.ToString());
        }
    }
}
