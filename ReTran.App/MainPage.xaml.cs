using ReTran_App.Services.Camera;

namespace ReTran_App
{
    public partial class MainPage : ContentPage
    {
        public MainPage()
        {
            InitializeComponent();
        }

        protected override void OnAppearing()
        {
            base.OnAppearing();
            // Register the virtual camera per-user at startup — best-effort, silent when the DLL is absent.
            // Đăng ký camera ảo per-user lúc mở app — best-effort, thiếu DLL thì bỏ qua.
            new CameraRegistrar().EnsureRegistered();
        }
    }
}
