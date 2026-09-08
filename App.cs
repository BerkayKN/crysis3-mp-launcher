using System;
using System.CodeDom.Compiler;
using System.Diagnostics;
using System.Windows;

namespace Crysis3_MP_Launcher
{
	public class App : Application
	{
		[DebuggerNonUserCode]
		[GeneratedCode("PresentationBuildTasks", "4.0.0.0")]
		public void InitializeComponent()
		{
			base.StartupUri = new Uri("MainWindow.xaml", UriKind.Relative);
		}

		protected override void OnStartup(StartupEventArgs e)
		{
			base.OnStartup(e);

			AppDomain.CurrentDomain.UnhandledException += (s, args) =>
			{
				if (args.ExceptionObject is Exception ex)
				{
					Logger.LogError("Unhandled AppDomain exception", ex);
					MessageBox.Show($"Fatal Error: {ex.Message}\n\nCheck launcher.log for details.", "Launcher Error", MessageBoxButton.OK, MessageBoxImage.Error);
				}
			};

			DispatcherUnhandledException += (s, args) =>
			{
				Logger.LogError("Unhandled Dispatcher exception", args.Exception);
				MessageBox.Show($"Error: {args.Exception.Message}\n\nCheck launcher.log for details.", "Launcher Error", MessageBoxButton.OK, MessageBoxImage.Error);
				args.Handled = true;
			};

			System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, args) =>
			{
				Logger.LogError("Unobserved Task exception", args.Exception);
				args.SetObserved();
			};
		}

		[STAThread]
		[DebuggerNonUserCode]
		[GeneratedCode("PresentationBuildTasks", "4.0.0.0")]
		public static void Main()
		{
			App app = new App();
			app.InitializeComponent();
			app.Run();
		}
	}
}
