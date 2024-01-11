using System.Configuration;
using System.Data;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Configuration;
using Sofar.LoggerManager;


namespace SerialOscilloscope
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            var configuration = new ConfigurationBuilder().AddJsonFile("appsettings.json").Build();
            

            AppDomain.CurrentDomain.UnhandledException += OnCurrentDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += OnTaskSchedulerUnobservedTaskException;
            DispatcherUnhandledException += OnDispatcherUnhandledException;
        }

        private void OnTaskSchedulerUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            var exception = e.Exception;
            var exceptionMessage = exception?.Message ?? "An unmanaged exception occured.";
            LoggerManager.Instance.DefaultLogger.Error(exceptionMessage);
            ExceptionDialog.HandleException(exception!);
        }

        private void OnCurrentDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var exception = e.ExceptionObject as Exception;
            var terminatingMessage = e.IsTerminating ? " The application is terminating." : string.Empty;
            var exceptionMessage = exception?.Message ?? "An unmanaged exception occured.";
            var message = string.Concat(exceptionMessage, terminatingMessage);
            LoggerManager.Instance.DefaultLogger.Error(message);
            ExceptionDialog.HandleException(exception!);
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            LoggerManager.Instance.DefaultLogger.Error(e.Exception, e.Exception.Message);
            ExceptionDialog.HandleException(e.Exception);
            e.Handled = true;
        }
    }

}
