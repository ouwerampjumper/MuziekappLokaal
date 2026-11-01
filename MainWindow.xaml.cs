using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace MuziekappLokaal
{
    public class Track
    {
        public string FilePath { get; set; }
        public string Title { get; set; }
        public string Album { get; set; }
        public string Artist { get; set; }
        public uint TrackNumber { get; set; }
    }

    public class AlbumGroup
    {
        public string AlbumName { get; set; }
        public string ArtistName { get; set; }
        public ImageSource CoverImage { get; set; }
        public List<Track> Tracks { get; set; } = new List<Track>();
    }

    public partial class MainWindow : Window
    {
        private MediaPlayer mediaPlayer = new MediaPlayer();
        private bool isPlaying = false;
        private bool isDraggingSlider = false;
        private bool isShuffleOn = false;
        private bool isFSQueueVisible = false;

        private string currentNavView = "Home"; // Houdt bij waar we waren voor we een album aanklikten
        private AlbumGroup currentViewedAlbum;  // Het album dat we momenteel bekijken

        private List<AlbumGroup> fullLibrary = new List<AlbumGroup>();
        private List<Track> currentQueue = new List<Track>();
        private List<Track> originalQueue = new List<Track>();
        private int currentIndex = -1;
        private DispatcherTimer timer;

        private List<string> savedFolders = new List<string>();
        private string settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MuziekappLokaal", "folders.txt");

        public MainWindow()
        {
            InitializeComponent();

            timer = new DispatcherTimer();
            timer.Interval = TimeSpan.FromMilliseconds(500);
            timer.Tick += Timer_Tick;

            mediaPlayer.MediaEnded += MediaPlayer_MediaEnded;
            mediaPlayer.MediaOpened += MediaPlayer_MediaOpened;
            mediaPlayer.Volume = VolumeSlider.Value;

            this.Loaded += MainWindow_Loaded;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (File.Exists(settingsPath))
            {
                savedFolders = File.ReadAllLines(settingsPath).Where(Directory.Exists).ToList();
                if (savedFolders.Count > 0)
                {
                    await LoadAllFoldersAsync();
                }
            }
        }

        // --- VENSTER NAVIGATIE & KNOPPEN ---
        private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void MaximizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        private void CloseButton_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

        private void NavHome_Click(object sender, RoutedEventArgs e)
        {
            currentNavView = "Home";
            HomeView.Visibility = Visibility.Visible;
            LibraryView.Visibility = Visibility.Collapsed;
            AlbumDetailsView.Visibility = Visibility.Collapsed;
            HomeAlbumsList.SelectedItem = null;
            HomeRecommendedList.SelectedItem = null;
        }

        private void NavLibrary_Click(object sender, RoutedEventArgs e)
        {
            currentNavView = "Library";
            HomeView.Visibility = Visibility.Collapsed;
            LibraryView.Visibility = Visibility.Visible;
            AlbumDetailsView.Visibility = Visibility.Collapsed;
            LibraryGridList.SelectedItem = null;
        }

        private void OpenFullscreen_Click(object sender, RoutedEventArgs e)
        {
            StandardUI.Visibility = Visibility.Collapsed;
            FullscreenUI.Visibility = Visibility.Visible;

            // Scroll wachtrij naar huidig nummer
            if (currentIndex >= 0 && FS_QueueList.Items.Count > 0)
            {
                FS_QueueList.ScrollIntoView(FS_QueueList.Items[currentIndex]);
            }
        }

        private void CloseFullscreen_Click(object sender, RoutedEventArgs e)
        {
            FullscreenUI.Visibility = Visibility.Collapsed;
            StandardUI.Visibility = Visibility.Visible;
        }

        // --- MAP IMPORTEREN & DATA LADEN ---
        private async void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            OpenFolderDialog folderDialog = new OpenFolderDialog();
            if (folderDialog.ShowDialog() == true)
            {
                if (!savedFolders.Contains(folderDialog.FolderName))
                {
                    savedFolders.Add(folderDialog.FolderName);
                    Directory.CreateDirectory(Path.GetDirectoryName(settingsPath));
                    File.WriteAllLines(settingsPath, savedFolders);
                }
                await LoadAllFoldersAsync();
            }
        }

        private async Task LoadAllFoldersAsync()
        {
            TopTitleArtistText.Text = "Bibliotheek laden...";
            string[] extensions = { ".mp3", ".wav", ".m4a", ".flac" };

            var allFiles = new List<string>();
            foreach (var folder in savedFolders)
            {
                if (Directory.Exists(folder))
                {
                    allFiles.AddRange(Directory.GetFiles(folder, "*.*", SearchOption.AllDirectories)
                                               .Where(f => extensions.Contains(Path.GetExtension(f).ToLower())));
                }
            }

            var loadedLibrary = await Task.Run(() =>
            {
                var tracks = new List<Track>();
                var albumCovers = new Dictionary<string, byte[]>();

                foreach (var file in allFiles)
                {
                    try
                    {
                        using (var tfile = TagLib.File.Create(file))
                        {
                            string alb = string.IsNullOrEmpty(tfile.Tag.Album) ? "Onbekend Album" : tfile.Tag.Album;
                            tracks.Add(new Track
                            {
                                FilePath = file,
                                Title = string.IsNullOrEmpty(tfile.Tag.Title) ? Path.GetFileNameWithoutExtension(file) : tfile.Tag.Title,
                                Album = alb,
                                Artist = tfile.Tag.FirstPerformer ?? "Onbekende Artiest",
                                TrackNumber = tfile.Tag.Track
                            });

                            if (!albumCovers.ContainsKey(alb) && tfile.Tag.Pictures.Length > 0)
                            {
                                albumCovers[alb] = tfile.Tag.Pictures[0].Data.Data;
                            }
                        }
                    }
                    catch { }
                }

                return tracks.GroupBy(t => t.Album)
                             .Select(g => new AlbumGroup
                             {
                                 AlbumName = g.Key,
                                 ArtistName = g.First().Artist,
                                 // Sorteren op tracknummer voor metadata volgorde
                                 Tracks = g.OrderBy(t => t.TrackNumber).ThenBy(t => t.Title).ToList(),
                             })
                             .Select(a => new { Album = a, CoverBytes = albumCovers.ContainsKey(a.AlbumName) ? albumCovers[a.AlbumName] : null })
                             .OrderBy(x => x.Album.ArtistName).ToList();
            });

            fullLibrary.Clear();
            foreach (var item in loadedLibrary)
            {
                if (item.CoverBytes != null)
                {
                    BitmapImage bitmap = new BitmapImage();
                    using (MemoryStream ms = new MemoryStream(item.CoverBytes))
                    {
                        bitmap.BeginInit();
                        bitmap.StreamSource = ms;
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.EndInit();
                        bitmap.Freeze();
                    }
                    item.Album.CoverImage = bitmap;
                }
                fullLibrary.Add(item.Album);
            }

            HomeAlbumsList.ItemsSource = fullLibrary;
            LibraryGridList.ItemsSource = fullLibrary;

            var recommended = new List<AlbumGroup>(fullLibrary);
            recommended.Reverse();
            HomeRecommendedList.ItemsSource = recommended;

            TopTitleArtistText.Text = "Geen nummer - Selecteer muziek";
        }

        // --- ALBUM DETAILS & AFSPELEN ---
        private void Album_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var listBox = sender as ListBox;
            if (listBox != null && listBox.SelectedItem is AlbumGroup selectedAlbum)
            {
                currentViewedAlbum = selectedAlbum;

                // Vul de detail weergave
                DetailAlbumArt.Source = currentViewedAlbum.CoverImage;
                DetailAlbumTitle.Text = currentViewedAlbum.AlbumName;
                DetailAlbumArtist.Text = currentViewedAlbum.ArtistName;
                DetailTrackList.ItemsSource = currentViewedAlbum.Tracks;

                // Verberg Home/Library, toon Album Details
                HomeView.Visibility = Visibility.Collapsed;
                LibraryView.Visibility = Visibility.Collapsed;
                AlbumDetailsView.Visibility = Visibility.Visible;
            }
        }

        private void BackFromAlbum_Click(object sender, RoutedEventArgs e)
        {
            AlbumDetailsView.Visibility = Visibility.Collapsed;
            if (currentNavView == "Home")
            {
                HomeView.Visibility = Visibility.Visible;
                HomeAlbumsList.SelectedItem = null;
                HomeRecommendedList.SelectedItem = null;
            }
            else
            {
                LibraryView.Visibility = Visibility.Visible;
                LibraryGridList.SelectedItem = null;
            }
        }

        private void PlayAlbum_Click(object sender, RoutedEventArgs e)
        {
            if (currentViewedAlbum == null || currentViewedAlbum.Tracks.Count == 0) return;

            SetQueueAndPlay(currentViewedAlbum.Tracks, 0);
        }

        private void DetailTrackList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (DetailTrackList.SelectedItem is Track selectedTrack)
            {
                // Speel specifiek nummer af uit dit album
                int startIdx = currentViewedAlbum.Tracks.IndexOf(selectedTrack);
                if (startIdx >= 0)
                {
                    SetQueueAndPlay(currentViewedAlbum.Tracks, startIdx);
                }
            }
        }

        private void SetQueueAndPlay(List<Track> newTracks, int startIndex)
        {
            originalQueue = new List<Track>(newTracks);
            currentQueue = new List<Track>(originalQueue);

            if (isShuffleOn)
            {
                var rnd = new Random();
                currentQueue = currentQueue.OrderBy(x => rnd.Next()).ToList();
                startIndex = currentQueue.IndexOf(originalQueue[startIndex]);
            }

            FS_QueueList.ItemsSource = currentQueue;
            PlayTrack(startIndex);
        }

        private void PlayTrack(int index)
        {
            if (index < 0 || index >= currentQueue.Count) return;

            currentIndex = index;
            Track track = currentQueue[currentIndex];

            FS_QueueList.SelectedIndex = currentIndex;

            mediaPlayer.Open(new Uri(track.FilePath));

            // Top Bar updaten
            TopTitleArtistText.Text = $"{track.Title} - {track.Artist}";

            // Fullscreen speler updaten
            FS_TitleText.Text = track.Title;
            FS_ArtistText.Text = track.Artist;

            var albumGroup = fullLibrary.FirstOrDefault(a => a.AlbumName == track.Album);
            if (albumGroup != null)
            {
                FS_AlbumArt.Source = albumGroup.CoverImage;
            }

            mediaPlayer.Play();
            isPlaying = true;
            PlayPauseBtn.Content = "⏸";
            FS_PlayPauseBtn.Content = "⏸";
            timer.Start();
        }

        // --- FULLSCREEN WACHTRIJ ---
        private void ToggleFSQueue_Click(object sender, RoutedEventArgs e)
        {
            isFSQueueVisible = !isFSQueueVisible;
            if (isFSQueueVisible)
            {
                // Wachtrij tonen, foto kleiner
                FSRowImage.Height = new GridLength(150);
                FSRowQueue.Height = new GridLength(1, GridUnitType.Star);
                if (currentIndex >= 0 && FS_QueueList.Items.Count > 0)
                    FS_QueueList.ScrollIntoView(FS_QueueList.Items[currentIndex]);
            }
            else
            {
                // Originele weergave herstellen
                FSRowImage.Height = new GridLength(1, GridUnitType.Star);
                FSRowQueue.Height = new GridLength(0);
            }
        }

        private void FS_QueueList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (FS_QueueList.SelectedIndex >= 0 && FS_QueueList.SelectedIndex != currentIndex)
            {
                PlayTrack(FS_QueueList.SelectedIndex);
            }
        }

        // --- TRANSPORT CONTROLS ---
        private void PlayPause_Click(object sender, RoutedEventArgs e)
        {
            if (mediaPlayer.Source == null && currentQueue.Count > 0)
            {
                PlayTrack(0);
                return;
            }

            if (isPlaying)
            {
                mediaPlayer.Pause();
                isPlaying = false;
                PlayPauseBtn.Content = "▶";
                FS_PlayPauseBtn.Content = "▶";
                timer.Stop();
            }
            else
            {
                mediaPlayer.Play();
                isPlaying = true;
                PlayPauseBtn.Content = "⏸";
                FS_PlayPauseBtn.Content = "⏸";
                timer.Start();
            }
        }

        private void Prev_Click(object sender, RoutedEventArgs e)
        {
            if (currentQueue.Count == 0) return;
            int newIndex = currentIndex - 1;
            if (newIndex < 0) newIndex = currentQueue.Count - 1;
            PlayTrack(newIndex);
        }

        private void Next_Click(object sender, RoutedEventArgs e)
        {
            if (currentQueue.Count == 0) return;
            int newIndex = currentIndex + 1;
            if (newIndex >= currentQueue.Count) newIndex = 0;
            PlayTrack(newIndex);
        }

        private void Shuffle_Click(object sender, RoutedEventArgs e)
        {
            isShuffleOn = !isShuffleOn;
            ShuffleBtn.Background = isShuffleOn ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E5A00D"))
                                                : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#333"));

            if (currentQueue.Count == 0) return;
            var currentTrack = currentIndex >= 0 && currentIndex < currentQueue.Count ? currentQueue[currentIndex] : null;

            if (isShuffleOn)
            {
                var rnd = new Random();
                currentQueue = currentQueue.OrderBy(x => rnd.Next()).ToList();
            }
            else
            {
                currentQueue = new List<Track>(originalQueue);
            }

            FS_QueueList.ItemsSource = null;
            FS_QueueList.ItemsSource = currentQueue;

            if (currentTrack != null)
            {
                currentIndex = currentQueue.IndexOf(currentTrack);
                FS_QueueList.SelectedIndex = currentIndex;
            }
        }

        private void MediaPlayer_MediaEnded(object sender, EventArgs e) => Next_Click(null, null);

        // --- VOORTGANG & VOLUME ---
        private void MediaPlayer_MediaOpened(object sender, EventArgs e)
        {
            if (mediaPlayer.NaturalDuration.HasTimeSpan)
            {
                ProgressSlider.Maximum = mediaPlayer.NaturalDuration.TimeSpan.TotalSeconds;
                TotalTimeText.Text = mediaPlayer.NaturalDuration.TimeSpan.ToString(@"m\:ss");
            }
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            if (mediaPlayer.Source != null && mediaPlayer.NaturalDuration.HasTimeSpan && !isDraggingSlider)
            {
                ProgressSlider.Value = mediaPlayer.Position.TotalSeconds;
                CurrentTimeText.Text = mediaPlayer.Position.ToString(@"m\:ss");
            }
        }

        private void ProgressSlider_MouseDown(object sender, MouseButtonEventArgs e) => isDraggingSlider = true;

        private void ProgressSlider_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (mediaPlayer.Source != null && mediaPlayer.NaturalDuration.HasTimeSpan)
            {
                mediaPlayer.Position = TimeSpan.FromSeconds(ProgressSlider.Value);
                CurrentTimeText.Text = mediaPlayer.Position.ToString(@"m\:ss");
            }
            isDraggingSlider = false;
        }

        private void ProgressSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (isDraggingSlider)
                CurrentTimeText.Text = TimeSpan.FromSeconds(ProgressSlider.Value).ToString(@"m\:ss");
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (mediaPlayer != null) mediaPlayer.Volume = VolumeSlider.Value;
        }
    }
}