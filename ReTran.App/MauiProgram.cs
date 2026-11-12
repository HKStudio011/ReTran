using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.LifecycleEvents;
using ReTran_App.Core;
using ReTran_App.Data;
using ReTran_App.Services;
using ReTran_App.Services.Diagnostics;
using ReTran_App.Services.FloatingMenu;
using ReTran_App.Services.OverlayFeed;
using ReTran_App.Services.OverlayWindow;
using ReTran_App.Services.Setup;
using ReTran_App.Services.Tray;
using ReTran_App.Services.Update;
using ReTran_App.Services.Window;

namespace ReTran_App
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                });

            builder.Services.AddMauiBlazorWebView();

            builder.Services.AddSingleton<JsInteropService>();
            builder.Services.AddSingleton<I18nService>();
            builder.Services.AddSingleton<ToastService>();
            builder.Services.AddSingleton<OverlayState>();
            builder.Services.AddSingleton<PreviewState>();

            // OBS overlay feed: loopback server serving the transparent /overlay page + SSE /events.
            // Feed overlay OBS: server loopback phục vụ trang trong suốt /overlay + SSE /events.
            // Một công tắc OverlayOnGame điều khiển cả overlay desktop lẫn feed OBS (spec K8): gate
            // phải là singleton dùng chung, nếu không hai bề mặt sẽ thấy hai giá trị khác nhau.
            // One OverlayOnGame switch drives both the desktop overlay and the OBS feed (spec K8): the
            // gate must be one shared singleton, or the two surfaces would see different values.
            builder.Services.AddSingleton<IOverlayVisibility, OverlayVisibility>();
            builder.Services.AddSingleton<OverlayFeedServer>();

            // Local game overlay: layered Win32 window + window/monitor anchor tracker.
            // Overlay game local: cửa sổ layered Win32 + tracker neo theo cửa sổ/monitor.
            // Win32OverlayHost mang HAI bề mặt — box overlay qua IOverlayHost và cửa sổ menu nổi qua
            // IMenuWindowHost — trên cùng một worker thread/pump, nên nó đăng ký theo TYPE CỤ THỂ rồi
            // mỗi interface forward tới đúng instance đó. Dạng đóng AddSingleton<IOverlayHost,
            // Win32OverlayHost>() sẽ cho ra HAI object khác nhau (hai worker thread, hai cửa sổ box,
            // và neo z-order box-dưới-menu trong BoxInsertAfter mất tác dụng).
            // Win32OverlayHost carries TWO surfaces — the overlay box via IOverlayHost and the floating
            // menu window via IMenuWindowHost — on one worker thread/pump, so it is registered by
            // CONCRETE type and each interface forwards to that one instance. The closed form would
            // produce two different objects (two worker threads and the box-under-menu z-order anchor
            // in BoxInsertAfter stops working).
            builder.Services.AddSingleton<Win32OverlayHost>();
            builder.Services.AddSingleton<IOverlayHost>(sp => sp.GetRequiredService<Win32OverlayHost>());
            builder.Services.AddSingleton<IMenuWindowHost>(sp => sp.GetRequiredService<Win32OverlayHost>());
            builder.Services.AddSingleton<IAnchorTracker, Win32AnchorTracker>();
            builder.Services.AddSingleton<IForegroundWatcher, Win32ForegroundWatcher>();
            builder.Services.AddSingleton<OverlayWindowService>();
            // Công tắc overlay cho menu nổi đi qua IOverlayToggle, trỏ cùng instance OverlayWindowService
            // mà trang điều khiển và tray đang dùng — state bật/tắt overlay chỉ được tồn tại ở một nơi.
            // The floating menu reaches the overlay through IOverlayToggle, forwarded to the same
            // OverlayWindowService instance the control page and the tray already use — the on/off state
            // must exist in exactly one place.
            builder.Services.AddSingleton<IOverlayToggle>(sp => sp.GetRequiredService<OverlayWindowService>());

            // Menu nổi (kế hoạch floating menu): RunStats là nguồn số DUY NHẤT cho cả rail thống kê
            // trang điều khiển lẫn vùng thống kê của menu; FloatingMenuService đứng sau switch trong
            // app, mục tray và cửa sổ menu. Cả hai là singleton không tham số khởi tạo — mọi dependency
            // do container cung cấp (xem IPipelineControl forward dưới: RunStats và FloatingMenuService
            // phụ thuộc interface, không phụ thuộc PipelineController cụ thể).
            // Floating menu: RunStats is the SINGLE numbers source for both the control page's stats rail
            // and the menu's stats block; FloatingMenuService backs the in-app switch, the tray entry and
            // the menu window. Both are parameterless singletons — every dependency comes from the
            // container (see the IPipelineControl forward below: RunStats and FloatingMenuService depend
            // on the interface, not on the concrete PipelineController).
            builder.Services.AddSingleton<RunStats>();
            builder.Services.AddSingleton<FloatingMenuService>();

            // System tray: close-to-tray window bridge, Shell_NotifyIcon host, clean exit path,
            // error routing (balloon vs toast) and the menu actions behind it. The icon is only
            // registered from the primary-window hook (below).
            // Khay hệ thống: bridge cửa sổ close-to-tray, host Shell_NotifyIcon, đường thoát sạch,
            // định tuyến lỗi (balloon vs toast) và các hành động menu đứng sau đó. Icon chỉ được
            // đăng ký từ hook cửa sổ chính (bên dưới).
            builder.Services.AddSingleton<AppWindowBridge>();
            builder.Services.AddSingleton<ITrayHost, Win32TrayHost>();
            builder.Services.AddSingleton<ITrayExit, TrayExitService>();
            builder.Services.AddSingleton(sp => new AppNotifier(
                sp.GetRequiredService<ToastService>(),
                sp.GetRequiredService<ITrayHost>(),
                () => sp.GetRequiredService<AppWindowBridge>().IsVisible,
                () => sp.GetRequiredService<AppWindowBridge>().IsForeground,
                sp.GetRequiredService<ILogger<AppNotifier>>()));
            builder.Services.AddSingleton<ITrayActions, AppTrayActions>();
            builder.Services.AddSingleton<TrayIconService>();

            string dbPath = TranslationCache.DefaultDbPath;
            builder.Services.AddDbContext<RetranDbContext>(
                options => options.UseSqlite($"Data Source={dbPath}"),
                ServiceLifetime.Singleton,
                ServiceLifetime.Singleton);
            builder.Services.AddSingleton<AppSettingsService>();
            builder.Services.AddSingleton<CoreProcessClient>();
            builder.Services.AddSingleton<ICorePeer>(sp => sp.GetRequiredService<CoreProcessClient>());
            builder.Services.AddSingleton<TranslationCache>();
            builder.Services.AddSingleton<PipelineController>();
            // Cùng instance cho bề mặt interface mà RunStats / FloatingMenuService / menu nổi cần —
            // đăng ký đóng sẽ tạo controller thứ hai (state chạy và nguồn hoạt động sẽ chia làm hai).
            // Same instance as the interface surface RunStats / FloatingMenuService depend on — the
            // closed form would build a second controller (run state and active source would split in two).
            builder.Services.AddSingleton<IPipelineControl>(sp => sp.GetRequiredService<PipelineController>());

            builder.Services.AddSingleton(_ => new HttpClient());
            builder.Services.AddSingleton<GitHubAppUpdateService>();
            builder.Services.AddSingleton<IAppUpdateService>(sp => sp.GetRequiredService<GitHubAppUpdateService>());
            builder.Services.AddSingleton<UpdateStopper>();
            builder.Services.AddSingleton<IUpdateStopper>(sp => sp.GetRequiredService<UpdateStopper>());
            builder.Services.AddSingleton<UpdateCoordinator>();
            builder.Services.AddSingleton<UpdateCheckService>();
            builder.Services.AddSingleton<IAppShutdown, MauiAppShutdown>();

            // First-run setup wizard (spec 2026-11-04): WMI adapter inventory, uv sync runner over the
            // real process runner, the core restart bridge and the coordinator itself. "Bỏ qua" (spec
            // §3.6) quits through the same clean tray-exit path as the tray's "Thoát": BeginExit on the
            // window bridge, then stop the services/core tree and quit — StopApplication alone only
            // stopped the host and left the MAUI window open. The disk/network seams are fail-safe —
            // an unreadable value fails the wizard's gate with a clear message instead of assuming OK.
            // Wizard setup chạy lần đầu (spec 2026-11-04): danh sách adapter WMI, runner uv sync trên
            // process runner thật, bridge restart core và chính coordinator. "Bỏ qua" (spec §3.6)
            // thoát qua đúng đường thoát sạch của tray ("Thoát"): BeginExit trên bridge cửa sổ, rồi
            // dừng service/cây core và quit — riêng StopApplication chỉ dừng host và để cửa sổ MAUI
            // mở. Seam đĩa/mạng fail-safe — giá trị không đọc được làm wizard fail cổng kèm thông báo
            // rõ thay vì giả định OK.
            builder.Services.AddSingleton<IVideoAdapterSource, VideoAdapterSource>();
            builder.Services.AddSingleton<IProcessRunner, ProcessRunner>();
            builder.Services.AddSingleton(sp => new UvSyncRunner(sp.GetRequiredService<IProcessRunner>()));
            builder.Services.AddSingleton<ISetupCoreBridge>(sp => new SetupCoreBridge(sp.GetRequiredService<ICorePeer>()));
            builder.Services.AddSingleton(sp => new SetupCoordinator(
                sp.GetRequiredService<IVideoAdapterSource>(),
                DiskFreeBytes,
                IsHostReachable,
                sp.GetRequiredService<UvSyncRunner>(),
                sp.GetRequiredService<ISetupCoreBridge>(),
                sp.GetRequiredService<AppSettingsService>(),
                () =>
                {
                    // Same order as AppTrayActions.ExitApp — a hard requirement, not a detail: without
                    // BeginExit the bridge's Closing handler turns IAppShutdown.Quit() back into
                    // hide-to-tray, so the app would stay alive in the tray instead of quitting.
                    // Cùng thứ tự như AppTrayActions.ExitApp — yêu cầu cứng, không phải chi tiết: nếu
                    // thiếu BeginExit, handler Closing của bridge biến IAppShutdown.Quit() trở lại
                    // thành ẩn xuống khay, app sẽ sống tiếp trong khay thay vì thoát.
                    sp.GetRequiredService<AppWindowBridge>().BeginExit();
                    _ = sp.GetRequiredService<ITrayExit>().ExitAsync(CancellationToken.None);
                },
                logger: sp.GetRequiredService<ILogger<SetupCoordinator>>()));

#if DEBUG
    		builder.Services.AddBlazorWebViewDeveloperTools();
    		// App logs must be observable in a Debug build. AddDebug cannot do that here: its
    		// Debug.WriteLine calls live in a Release-built NuGet assembly, so [Conditional("DEBUG")]
    		// strips them and every ILogger write vanishes without a trace (the 2026-11-13 tray
    		// investigation ran blind on exactly that — three captured sessions, zero app log lines).
    		// TraceLoggerProvider writes through Trace.WriteLine instead, compiled into this app's own
    		// Debug build, so the text reaches OutputDebugString/DebugView.
    		// Log của app phải quan sát được trong build Debug. AddDebug không làm được điều đó ở đây:
    		// các lời gọi Debug.WriteLine của nó nằm trong assembly NuGet build Release nên bị
    		// [Conditional("DEBUG")] strip, mọi lần ghi ILogger biến mất không dấu vết (cuộc điều tra
    		// tray 2026-11-13 đi mù đúng vì điều này — ba phiên capture, không một dòng log app nào).
    		// TraceLoggerProvider ghi qua Trace.WriteLine, được compile vào chính build Debug của app,
    		// nên nội dung tới được OutputDebugString/DebugView.
    		builder.Logging.AddProvider(new TraceLoggerProvider());
#endif

            // Hook the primary window: pressing X hides it to the tray, and the tray icon is
            // registered on the UI thread — exactly where the host's message-only window is created
            // (no new thread, no timer). Only ordinal 0 is hooked (see WindowHooks): a second
            // window would mean a second icon and a double hide. The counter starts at -1 because
            // Interlocked.Increment hands 0 to the first window.
            // Hook cửa sổ chính: bấm X ẩn cửa sổ xuống khay, và icon khay được đăng ký trên UI
            // thread — đúng nơi cửa sổ message-only của host được tạo (không thread mới, không
            // timer). Chỉ ordinal 0 được hook (xem WindowHooks): window thứ hai sẽ sinh icon thứ hai
            // và ẩn hai lần. Bộ đếm bắt đầu ở -1 vì Interlocked.Increment trả 0 cho window đầu tiên.
            int windowOrdinal = -1;
            builder.ConfigureLifecycleEvents(events => events.AddWindows(w => w.OnWindowCreated(window =>
            {
                var ordinal = Interlocked.Increment(ref windowOrdinal);
                if (!WindowHooks.ShouldHookWindow(ordinal)) return; // chỉ window chính (Review Focus #5)

                IServiceProvider? services = IPlatformApplication.Current?.Services; // pattern repo, xem App.xaml.cs:10
                if (services is null) return;

                // The close-to-tray bridge and the tray icon start in SEPARATE try blocks, on purpose:
                // they fail independently. Attach activates WinRT (GetWindowHandle/GetWindowIdFromWindow)
                // and can throw; sharing one try made every Attach failure skip TrayIconService.Start()
                // entirely — the icon would never reach the shell while the floating menu (its own try)
                // still started: exactly the "menu OK, tray missing" shape this hook must not be able
                // to produce (investigation 2026-11-13; live instrumentation caught no Attach throw on
                // that build, so the split removes the latent failure mode, it was not the observed
                // cause). A failed bridge now costs only close-to-tray (the icon still appears), and a
                // failed icon start no longer costs close-to-tray. Neither branch may kill the app:
                // both are conveniences, the app is not.
                // Bridge close-to-tray và icon khay start trong HAI try RIÊNG, có chủ đích: chúng hỏng
                // độc lập. Attach kích hoạt WinRT (GetWindowHandle/GetWindowIdFromWindow) và có thể ném
                // lỗi; khi còn chung một try, mọi lỗi Attach đều bỏ qua TrayIconService.Start() — icon
                // không bao giờ tới được shell trong khi menu nổi (try riêng) vẫn start: đúng hình dạng
                // "menu OK, tray không có" mà hook này không được phép tạo ra (điều tra 2026-11-13;
                // instrumentation live không bắt được lỗi Attach nào trên build đó, nên việc tách try
                // loại bỏ một failure mode tiềm ẩn, không phải nguyên nhân đã quan sát). Bridge hỏng giờ
                // chỉ mất close-to-tray (icon vẫn hiện), và lần start icon hỏng không còn kéo theo
                // close-to-tray. Không nhánh nào được giết app: cả hai đều là tiện ích, app thì không.
                try
                {
                    services.GetRequiredService<AppWindowBridge>().Attach(window);
                }
                catch (Exception ex)
                {
                    IPlatformApplication.Current?.Services?
                        .GetService<ILoggerFactory>()?
                        .CreateLogger("ReTran.App.Tray")
                        .LogError(ex, "The close-to-tray window hook (AppWindowBridge.Attach) failed on window creation; the app continues without close-to-tray.");
                }

                try
                {
                    services.GetRequiredService<TrayIconService>().Start();
                }
                catch (Exception ex)
                {
                    IPlatformApplication.Current?.Services?
                        .GetService<ILoggerFactory>()?
                        .CreateLogger("ReTran.App.Tray")
                        .LogError(ex, "The tray icon start failed on window creation; the app continues without the tray icon.");
                }

                try
                {
                    // Menu nổi: cùng services đã resolve và cùng cửa sổ chính. Start() tạo cửa sổ menu
                    // (idempotent) trên worker thread của host overlay rồi hiện nó ở vị trí đã lưu khi
                    // cờ FloatingMenuOn bật. try riêng: hỏng ở đây không được giết app (menu là tiện
                    // ích, như nhánh tray trên), và không được ghi log bằng message của tray — tray đã
                    // chạy xong nên message đó sẽ báo sai thứ đã hỏng.
                    // Floating menu: same resolved services and same primary window. Start() creates the
                    // menu window (idempotent) on the overlay host's worker thread and shows it at the
                    // persisted position when FloatingMenuOn is on. Separate try: a failure here must not
                    // kill the app (the menu is a convenience, like the tray branches above) and must not
                    // be logged with the tray's message — the tray already finished, so that message
                    // would misreport what failed.
                    services.GetRequiredService<FloatingMenuService>().Start();
                }
                catch (Exception ex)
                {
                    IPlatformApplication.Current?.Services?
                        .GetService<ILoggerFactory>()?
                        .CreateLogger("ReTran.App.FloatingMenu")
                        .LogError(ex, "The floating menu failed to start on window creation; the app continues without it.");
                }
            })));

            MauiApp app = builder.Build();
            MigrateDatabase(app.Services, dbPath);
            return app;
        }

        // Dedicated probe client for the setup Checking step: 15 s timeout — the slow paddle host
        // (www.paddlepaddle.org.cn answered in ~8.2 s, measured live 2026-11-11, while pypi ~1.5 s and
        // pytorch ~0.25 s stayed fast) made the old 3 s value fail the wizard's network gate
        // spuriously 3/3 times. The gate walks SystemCheck.NetworkProbeHosts (3 hosts: pypi,
        // paddlepaddle.org.cn, github) SEQUENTIALLY, so the real worst case is 3 hung hosts at
        // 15 s each — up to 45 s — not a single 15 s; offline still fails fast (DNS errors
        // immediately) and only a hung host sits out its full timeout, which stays acceptable for
        // a one-time wizard check.
        // Probe client riêng cho bước Checking của setup: timeout 15 s — host paddle chậm
        // (www.paddlepaddle.org.cn trả lời ~8,2 s, đo thật 2026-11-11, trong khi pypi ~1,5 s và
        // pytorch ~0,25 s vẫn nhanh) làm mốc 3 s cũ chặn oan cổng mạng của wizard 3/3 lần. Cổng này
        // duyệt SystemCheck.NetworkProbeHosts (3 host: pypi, paddlepaddle.org.cn, github) TUẦN TỰ,
        // nên worst case thật là 3 host treo × 15 s — tối đa 45 s — không phải một lần 15 s; offline
        // vẫn fail nhanh (DNS lỗi ngay) và chỉ host treo mới chờ hết timeout của nó, vẫn chấp nhận
        // được cho một lần kiểm tra wizard.
        private static readonly HttpClient NetworkProbeClient = new() { Timeout = TimeSpan.FromSeconds(15) };

        /// <summary>
        /// Free bytes on the volume containing <paramref name="path"/>; 0 when unreadable (fail-safe:
        /// the wizard then fails its disk gate with a clear message instead of assuming space).
        /// Số byte trống trên ổ chứa path; 0 khi không đọc được (fail-safe: wizard fail cổng đĩa kèm
        /// thông báo rõ thay vì giả định có dung lượng).
        /// </summary>
        private static long DiskFreeBytes(string path)
        {
            try { return new DriveInfo(Path.GetFullPath(path)).AvailableFreeSpace; }
            catch (Exception) { return 0; }
        }

        /// <summary>
        /// HTTPS reachability probe for the setup network gate, one host at a time with the 15 s
        /// probe timeout: any HTTP answer (even 4xx/5xx) proves the host is reachable; DNS/TLS/connect
        /// failures report unreachable.
        /// Probe HTTPS cho cổng mạng của setup, mỗi lần một host với timeout 15 s của probe: bất kỳ
        /// đáp HTTP nào (kể cả 4xx/5xx) chứng minh tới được host; lỗi DNS/TLS/kết nối báo không tới được.
        /// </summary>
        private static bool IsHostReachable(string host)
        {
            try
            {
                using HttpResponseMessage response = NetworkProbeClient
                    .GetAsync($"https://{host}/", HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void MigrateDatabase(IServiceProvider services, string dbPath)
        {
            try
            {
                // TranslationCache.DefaultDbPath does not create the parent directory.
                string? dir = Path.GetDirectoryName(dbPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                services.GetRequiredService<RetranDbContext>().Database.Migrate();
            }
            catch (Exception ex)
            {
                services.GetService<ILoggerFactory>()?
                    .CreateLogger("ReTran.App.Startup")
                    .LogError(ex, "Database migration failed at startup; the translation cache is unavailable.");
            }
        }
    }
}
