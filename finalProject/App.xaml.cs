using finalProject.Models;
using System.Configuration;
using System.Data;
using System.Windows;

namespace finalProject
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            this.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // Worker 데이터 초기화
            WorkerManager.Initialize();
        }
    }

}
