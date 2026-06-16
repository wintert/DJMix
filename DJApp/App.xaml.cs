using DJAutoMixApp.Services;
using DJAutoMixApp.ViewModels;
using System.IO;
using System.Windows;

namespace DJAutoMixApp
{
    public partial class App : Application
    {
        // Keep delegate alive to prevent GC collection while C++ engine holds the function pointer
        private static AudioEngineInterop.TrackEndedCallback? _trackEndedHandler;

        public static void Log(string message)
        {
            try
            {
                var logDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
                System.IO.Directory.CreateDirectory(logDir);
                File.AppendAllText(System.IO.Path.Combine(logDir, "debug.log"), $"{DateTime.Now:HH:mm:ss} {message}\n");
            }
            catch
            {
                // Silently fail if logging is unavailable
            }
        }
        
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            Log("=== DJ App Starting ===");
            
            // Initialize C++ audio engine
            int result = AudioEngineInterop.engine_init(44100, 512);
            if (result != 0)
            {
                MessageBox.Show("Failed to initialize audio engine. Make sure DJAudioEngine.dll and its dependencies are present.",
                    "Audio Engine Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown();
                return;
            }

            // Start the audio engine (will look for ASIO device)
            result = AudioEngineInterop.engine_start();
            if (result != 0)
            {
                MessageBox.Show("Failed to start audio engine. An ASIO audio device is required.\n\n" +
                    "Install ASIO4ALL if you don't have an ASIO-compatible audio interface.",
                    "Audio Engine Error", MessageBoxButton.OK, MessageBoxImage.Error);
                AudioEngineInterop.engine_shutdown();
                Shutdown();
                return;
            }

            // Create audio deck controllers (using C++ engine)
            var deckA = new AudioDeck(0); // Deck ID 0
            var deckB = new AudioDeck(1); // Deck ID 1

            // Register C++ track-ended callback (keep delegate alive via static field)
            _trackEndedHandler = deckId =>
            {
                System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    if (deckId == 0) deckA.NotifyTrackEnded();
                    else deckB.NotifyTrackEnded();
                });
            };
            AudioEngineInterop.set_track_ended_callback(_trackEndedHandler);

            // Create other services
            var playlistManager = new PlaylistManager();
            var beatDetector = new BeatDetector();
            var autoMixEngine = new AutoMixEngine(deckA, deckB, playlistManager, beatDetector);

            // Create main ViewModel
            var mainViewModel = new MainViewModel(deckA, deckB, playlistManager, beatDetector, autoMixEngine);

            // Create and show main window
            var mainWindow = new MainWindow
            {
                DataContext = mainViewModel
            };
            mainWindow.Show();

            // Clean up on exit
            this.Exit += (s, args) =>
            {
                AudioEngineInterop.engine_stop();
                AudioEngineInterop.engine_shutdown();
            };
        }
    }
}
