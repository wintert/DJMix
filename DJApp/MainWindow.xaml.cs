using System.Windows;
using System.Linq;
using DJAutoMixApp.ViewModels;
using DJAutoMixApp.Services;

namespace DJAutoMixApp
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        private void Playlist_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        private void Playlist_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                
                if (DataContext is MainViewModel viewModel)
                {
                    viewModel.Playlist.HandleFileDrop(files);
                }
            }
        }

        // Deck A drag-drop handlers
        private void DeckA_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        private void DeckA_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                
                if (files.Length > 0 && DataContext is MainViewModel viewModel)
                {
                    LoadTrackOnDeck(files[0], "Deck A", viewModel);
                }
            }
        }

        // Deck B drag-drop handlers
        private void DeckB_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        private void DeckB_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                
                if (files.Length > 0 && DataContext is MainViewModel viewModel)
                {
                    LoadTrackOnDeck(files[0], "Deck B", viewModel);
                }
            }
        }

        private void LoadTrackOnDeck(string filePath, string deckName, MainViewModel viewModel)
        {
            // Check if it's an audio file
            var ext = System.IO.Path.GetExtension(filePath).ToLowerInvariant();
            if (ext != ".mp3" && ext != ".wav" && ext != ".m4a" && ext != ".ogg" && ext != ".flac")
            {
                MessageBox.Show("Please drop an audio file (MP3, WAV, M4A, OGG, or FLAC)", "Invalid File");
                return;
            }

            try
            {
                // Get the appropriate deck ViewModel and load track
                var deckViewModel = deckName == "Deck A" ? viewModel.DeckA : viewModel.DeckB;
                
                // Load track onto the deck (ViewModel handles analysis and waveform)
                deckViewModel.LoadTrack(filePath);
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"Error loading track: {ex.Message}", "Error");
            }
        }
    }
}
