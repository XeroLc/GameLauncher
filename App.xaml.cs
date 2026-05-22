using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using GameLauncher.Data;
using GameLauncher.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace GameLauncher
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        private Window? _window;

        public static Window? MainWindow { get; private set; }

        public static IServiceProvider Services { get; private set; }

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            var services = new ServiceCollection();

            services.AddSingleton<DatabaseContext>();
            services.AddSingleton<GameRepository>();
            services.AddSingleton<CollectionRepository>();
            services.AddSingleton<GameService>();
            services.AddSingleton<GmdFileService>();
            services.AddSingleton<ImageService>();
            services.AddSingleton<GameImageLoader>();
            services.AddSingleton<DataSyncService>();
            services.AddSingleton<UpdateCheckerService>();

            services.AddTransient<DiskScanService>();
            services.AddTransient<AutoScanService>();
            services.AddTransient<DataMigrationService>();
            services.AddTransient<DataConsistencyService>();

            Services = services.BuildServiceProvider();

            InitializeComponent();

            this.UnhandledException += App_UnhandledException;
        }

        private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine($"=== 未处理的异常 ===");
            System.Diagnostics.Debug.WriteLine($"类型: {e.Exception.GetType().FullName}");
            System.Diagnostics.Debug.WriteLine($"消息: {e.Exception.Message}");
            System.Diagnostics.Debug.WriteLine($"堆栈跟踪: {e.Exception.StackTrace}");

            if (e.Exception.InnerException != null)
            {
                System.Diagnostics.Debug.WriteLine($"内部异常: {e.Exception.InnerException.Message}");
                System.Diagnostics.Debug.WriteLine($"内部异常堆栈: {e.Exception.InnerException.StackTrace}");
            }

            e.Handled = true; // 阻止应用程序崩溃
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            _window = new MainWindow();
            MainWindow = _window;
            _window.Activate();
        }
    }
}
