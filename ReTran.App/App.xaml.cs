using ReTran_App.Services.Update;

namespace ReTran_App
{
    public partial class App : Application
    {
        public App()
        {
            InitializeComponent();
            UpdateCheckService? check = IPlatformApplication.Current?.Services?.GetService<UpdateCheckService>();
            check?.Start();
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            return new Window(new MainPage()) { Title = "ReTran App" };
        }
    }
}
