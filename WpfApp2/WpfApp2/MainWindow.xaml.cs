using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Runtime.InteropServices;
using Emgu.CV;
using Microsoft.Win32;
using Emgu.CV.CvEnum;
using System.Windows.Controls;
using System.Collections.ObjectModel;
using System.Drawing.Imaging;
using System.IO;
using LibVLCSharp.Shared;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Diagnostics;
using Path = System.IO.Path;
using System.Linq;
using System.ComponentModel;
using System.Windows.Data;
//using System.Guid;

namespace WpfApp2
{
    // 시간을 픽셀로 변환하는 컨버터
    public class TimeToPixelConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (value is double seconds && App.Current.MainWindow is MainWindow mainWindow)
            {
                return seconds * mainWindow.PixelsPerSecond;
            }
            return 0;
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (value is double pixels && App.Current.MainWindow is MainWindow mainWindow)
            {
                return pixels / mainWindow.PixelsPerSecond;
            }
            return 0;
        }
    }

    // 시간을 문자열로 변환하는 컨버터
    public class DurationConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (value is double seconds)
            {
                TimeSpan timeSpan = TimeSpan.FromSeconds(seconds);
                return $"{timeSpan.Minutes:00}:{timeSpan.Seconds:00}";
            }
            return "00:00";
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    // 트랙 인덱스를 Y 위치로 변환하는 컨버터
    public class TrackToPositionConverter : IValueConverter
    {
        private const int TRACK_HEIGHT = 60; // 각 트랙의 높이
        
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (value is int trackIndex)
            {
                return trackIndex * TRACK_HEIGHT; // 트랙 인덱스에 따라 Y 위치 계산
            }
            return 0;
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (value is double yPosition)
            {
                return (int)(yPosition / TRACK_HEIGHT); // Y 위치에서 트랙 인덱스 계산
            }
            return 0;
        }
    }

    public partial class MainWindow : Window
    {
        private DispatcherTimer _timer;
        private bool _isPlaying = false;
        private MainViewModel _mainViewModel;
        private EditFunction _editFunction;
        private string _currentVideoPath;
        private CancellationTokenSource _cts;
        private LibVLC _libVLC;
        private LibVLCSharp.Shared.MediaPlayer _mediaPlayer;
        private bool _isSeeking = false;
        private bool _isRendering = false;
        private bool _updatingUI = false;

        private const double DEFAULT_TIMELINE_SECONDS = 300; // 5분 = 300초
        private double _pixelsPerSecond = 10; // 1초당 10픽셀 (기본값)
        private double _zoomLevel = 1.0; // 확대/축소 레벨 (1.0 = 100%)
        private double _currentVideoLengthSec = DEFAULT_TIMELINE_SECONDS;

        // 클립 관련 필드
        private VideoClip _selectedClip;
        private bool _isDraggingClip = false;
        private double _originalClipStart;
        private VideoClip _copiedClip;

        // 픽셀 단위 관련 프로퍼티 (외부 컨버터에서 접근)
        public double PixelsPerSecond => _pixelsPerSecond;

        private Line _playheadLine;
        private string _ffmpegPath = @"C:\ffmpeg\bin\ffmpeg.exe";
        private string _ffprobePath = @"C:\ffmpeg\bin\ffprobe.exe";
        private string _thumbnailOutputDir;

        // XAML에 정의된 배속 컨트롤 참조
        private StackPanel _speedControlPanel;
        private TextBox _speedTextBox;
        private Slider _speedSlider;
        private TextBlock _speedValueText;

        private MyVideo _selectedVideo;

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            if (PresentationSource.FromVisual(this) is HwndSource hwndSource)
            {
            hwndSource.CompositionTarget.RenderMode = RenderMode.Default; // 가능하면 하드웨어 가속 사용
            }
        }

        public MainWindow()
        {
            InitializeComponent();
            InitializeApplication();
            InitializeControlReferences();
        }

        private void InitializeControlReferences()
        {
            // XAML에 정의된 컨트롤 참조 가져오기
            try
            {
                _speedControlPanel = this.FindName("SpeedControlPanel") as StackPanel;
                _speedTextBox = this.FindName("SpeedTextBox") as TextBox;
                _speedSlider = this.FindName("SpeedSlider") as Slider;
                _speedValueText = this.FindName("SpeedValueText") as TextBlock;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"컨트롤 참조 초기화 오류: {ex.Message}");
            }
        }

        private void InitializeApplication()
        {
            try
            {
                // ffmpeg 경로 확인
                VerifyFfmpegPaths();

                // EmguCV 초기화
            Core.Initialize();
                CvInvoke.UseOpenCL = true; // OpenCL 활성화, GPU 가속

                // LibVLC 초기화
            _libVLC = new LibVLC();
            _mediaPlayer = new LibVLCSharp.Shared.MediaPlayer(_libVLC);
            videoView.MediaPlayer = _mediaPlayer;
            _mediaPlayer.LengthChanged += MediaPlayer_LengthChanged;
                _mediaPlayer.EncounteredError += (s, e) => 
                {
                    MessageBox.Show("미디어 플레이어 오류가 발생했습니다.", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
                };

                // 편집 기능 초기화
                _editFunction = new EditFunction();

                // ViewModel 초기화
            _mainViewModel = new MainViewModel();
                DataContext = _mainViewModel;

                // 썸네일 디렉토리 설정
                _thumbnailOutputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "thumbnails");
                EnsureDirectoryExists(_thumbnailOutputDir);

                // 타임라인 초기화
            DrawTimelineRuler();

                // 이벤트 연결
                SizeChanged += Window_SizeChanged;
                StateChanged += Window_StateChanged;

            // 플레이헤드 초기화
                InitializePlayhead();

                // 볼륨 초기화
                if (_mediaPlayer != null && sliderVolume != null)
                {
                    _mediaPlayer.Volume = 70;
                    sliderVolume.Value = 0.7;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"애플리케이션 초기화 중 오류가 발생했습니다: {ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void VerifyFfmpegPaths()
        {
            // ffmpeg 경로 확인
            if (!File.Exists(_ffmpegPath))
            {
                string message = $"ffmpeg가 경로에 없습니다: {_ffmpegPath}\n다른 경로를 지정하시겠습니까?";
                var result = MessageBox.Show(message, "ffmpeg 오류", MessageBoxButton.YesNo, MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    OpenFileDialog fileDialog = new OpenFileDialog
                    {
                        Title = "ffmpeg.exe 선택",
                        Filter = "Executable files (*.exe)|*.exe",
                        FileName = "ffmpeg.exe"
                    };

                    if (fileDialog.ShowDialog() == true)
                    {
                        _ffmpegPath = fileDialog.FileName;
                    }
                }
            }

            // ffprobe 경로 확인
            if (!File.Exists(_ffprobePath))
            {
                string message = $"ffprobe가 경로에 없습니다: {_ffprobePath}\n다른 경로를 지정하시겠습니까?";
                var result = MessageBox.Show(message, "ffprobe 오류", MessageBoxButton.YesNo, MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    OpenFileDialog fileDialog = new OpenFileDialog
                    {
                        Title = "ffprobe.exe 선택",
                        Filter = "Executable files (*.exe)|*.exe",
                        FileName = "ffprobe.exe"
                    };

                    if (fileDialog.ShowDialog() == true)
                    {
                        _ffprobePath = fileDialog.FileName;
                    }
                }
            }
        }

        private void EnsureDirectoryExists(string directoryPath)
        {
            try
            {
                if (!Directory.Exists(directoryPath))
                {
                    Directory.CreateDirectory(directoryPath);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"디렉토리 생성 중 오류 발생: {ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void InitializePlayhead()
        {
            _playheadLine = new Line
            {
                Stroke = Brushes.Red,
                StrokeThickness = 2,
                Y1 = 0,
                Y2 = 30
            };

            if (PlayheadCanvas != null)
            {
                PlayheadCanvas.Children.Add(_playheadLine);
                Loaded += (s, e) => UpdatePlayheadClip();
            }
        }

        private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_playheadLine != null && TimelineScrollViewer != null)
            {
                _playheadLine.Y2 = TimelineScrollViewer.ActualHeight;
                UpdatePlayheadClip();
            }
        }

        private void Window_StateChanged(object sender, EventArgs e)
        {
            if (WindowState == WindowState.Maximized)
            {
                MaxHeight = SystemParameters.WorkArea.Height + 1;
                MaxWidth = SystemParameters.WorkArea.Width + 1;
            }
            else
            {
                MaxHeight = double.PositiveInfinity;
                MaxWidth = double.PositiveInfinity;
            }
        }

        #region 모델 클래스
        public class MainViewModel
        {
            public MyVideoViewModel VideoList { get; }
            public VideoEditorViewModel VideoEditor { get; }
            public ObservableCollection<string> Categories { get; }

            public MainViewModel()
            {
                VideoList = new MyVideoViewModel();
                VideoEditor = new VideoEditorViewModel();
                Categories = new ObservableCollection<string> { "미분류", "감정1", "감정2", "감정3", "감정4", "감정5" };
            }

            public void AddCategory(string category)
            {
                if (!string.IsNullOrWhiteSpace(category) && !Categories.Contains(category))
                {
                    Categories.Add(category);
                }
            }
        }

        public class MyVideo
        {
            public string name { get; set; }
            public string FullPath { get; set; }
            public string Category { get; set; } = "미분류";
        }

        public class MyVideoViewModel
        {
            public ObservableCollection<MyVideo> MyVideoes { get; }

            public MyVideoViewModel()
            {
                MyVideoes = new ObservableCollection<MyVideo>();
            }

            public void AddVideo(string fileName, string fullPath)
            {
                if (string.IsNullOrEmpty(fileName) || string.IsNullOrEmpty(fullPath))
                    return;

                MyVideo videoItem = new MyVideo { name = fileName, FullPath = fullPath };
                MyVideoes.Add(videoItem);
            }
        }

        public class ThumbnailItem
        {
            public BitmapImage Image { get; set; }
            public double TimePosition { get; set; }
            public string ImagePath { get; set; }
        }

        public class VideoEditorViewModel
        {
            public ObservableCollection<ThumbnailItem> Thumbnails { get; }
            public ObservableCollection<VideoClip> TimelineClips { get; }

            public VideoEditorViewModel()
            {
                Thumbnails = new ObservableCollection<ThumbnailItem>();
                TimelineClips = new ObservableCollection<VideoClip>();
            }
        }

        // 타임라인에 추가되는 비디오 클립을 나타내는 클래스
        public class VideoClip : INotifyPropertyChanged
        {
            private string _name;
            private double _startPosition; // 타임라인 상의 시작 위치 (초)
            private double _startTime;    // 원본 영상에서의 시작 지점 (초)
            private double _duration;     // 클립 길이 (초)
            private double _width;        // UI 너비 (픽셀)
            private string _videoPath;    // 원본 비디오 경로
            private BitmapImage _thumbnail; // 대표 썸네일
            private int _trackIndex;      // 트랙 인덱스 (0-4)

            public string Name 
            { 
                get => _name; 
                set { _name = value; OnPropertyChanged(nameof(Name)); }
            }

            public double StartPosition 
            { 
                get => _startPosition; 
                set 
                { 
                    _startPosition = value; 
                    // 너비도 함께 업데이트
                    if (App.Current.MainWindow is MainWindow mainWindow)
                    {
                        Width = Duration * mainWindow.PixelsPerSecond;
                    }
                    OnPropertyChanged(nameof(StartPosition)); 
                }
            }

            public double StartTime 
            { 
                get => _startTime; 
                set { _startTime = value; OnPropertyChanged(nameof(StartTime)); }
            }

            public double Duration 
            { 
                get => _duration; 
                set { _duration = value; OnPropertyChanged(nameof(Duration)); }
            }

            public double Width 
            { 
                get => _width; 
                set { _width = value; OnPropertyChanged(nameof(Width)); }
            }

            public string VideoPath 
            { 
                get => _videoPath; 
                set { _videoPath = value; OnPropertyChanged(nameof(VideoPath)); }
            }

            public BitmapImage Thumbnail 
            { 
                get => _thumbnail; 
                set { _thumbnail = value; OnPropertyChanged(nameof(Thumbnail)); }
            }
            
            public int TrackIndex
            {
                get => _trackIndex;
                set { _trackIndex = value; OnPropertyChanged(nameof(TrackIndex)); }
            }

            public string Category { get; set; } = "미분류";
            
            public Guid Id { get; } = Guid.NewGuid();

            // 클립 복사 메소드
            public VideoClip Clone()
            {
                return new VideoClip
                {
                    Name = this.Name + " (복사본)",
                    StartPosition = this.StartPosition,
                    StartTime = this.StartTime,
                    Duration = this.Duration,
                    Width = this.Width,
                    VideoPath = this.VideoPath,
                    Thumbnail = this.Thumbnail,
                    Category = this.Category,
                    TrackIndex = this.TrackIndex
                };
            }

            public event PropertyChangedEventHandler PropertyChanged;

            protected virtual void OnPropertyChanged(string propertyName)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }
        }
        #endregion

        #region 타임라인 관련 함수
        private void DrawTimelineRuler()
        {
            if (TimelineRulerCanvas == null || ThumbnailItemsControl == null) return;

            // 영상이 없어도 항상 5분 기준으로 그림
            double videoLength = Math.Max(_currentVideoLengthSec, DEFAULT_TIMELINE_SECONDS);
            double totalTimelineWidth = videoLength * _pixelsPerSecond;

            TimelineRulerCanvas.Children.Clear();
            TimelineRulerCanvas.Width = totalTimelineWidth;

            // 썸네일 StackPanel도 동일한 폭으로 맞춤
                ThumbnailItemsControl.Width = totalTimelineWidth;

            // 1초마다 얇은 선, 5초마다 굵은 선+숫자
            for (int sec = 0; sec <= videoLength; sec++)
            {
                double x = sec * _pixelsPerSecond;
                bool isMajorTick = sec % 5 == 0;

                var line = new Line
                {
                    X1 = x,
                    X2 = x,
                    Y1 = 0,
                    Y2 = isMajorTick ? 20 : 10,
                    Stroke = isMajorTick ? Brushes.LightGray : Brushes.Gray,
                    StrokeThickness = isMajorTick ? 2 : 1
                };

                TimelineRulerCanvas.Children.Add(line);

                // 5초마다 숫자 표시
                if (isMajorTick)
                {
                    var text = new TextBlock
                    {
                        Text = TimeSpan.FromSeconds(sec).ToString(@"m\:ss"),
                        Foreground = Brushes.White,
                        FontSize = 12
                    };
                    Canvas.SetLeft(text, x + 2);
                    Canvas.SetTop(text, 20);
                    TimelineRulerCanvas.Children.Add(text);
                }
            }
        }

        private void UpdatePlayheadClip()
        {
            if (TimelineScrollViewer == null || PlayheadCanvas == null) return;

            double horizontalOffset = TimelineScrollViewer.HorizontalOffset;
            var clipRect = new Rect(
                horizontalOffset,
                0,
                TimelineScrollViewer.ViewportWidth,
                TimelineScrollViewer.ViewportHeight
            );

            PlayheadCanvas.Clip = new RectangleGeometry(clipRect);
        }

        private void TimelineScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (TimelineRulerCanvas == null || PlayheadCanvas == null || TimelineScrollViewer == null) return;

            double maxOffset = TimelineRulerCanvas.ActualWidth - TimelineScrollViewer.ViewportWidth;
            double clampedOffset = Math.Clamp(e.HorizontalOffset, 0, maxOffset);
            TimelineRulerCanvas.Margin = new Thickness(-clampedOffset, 0, 0, 0);
            PlayheadCanvas.Margin = new Thickness(-clampedOffset, 0, 0, 0);
            UpdatePlayheadClip();
        }

        private void Timeline_DragOver(object sender, DragEventArgs e)
        {
            // StringFormat 타입의 데이터가 드래그 중이면 복사 가능 효과 표시
            e.Effects = e.Data.GetDataPresent(DataFormats.StringFormat) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private async void Timeline_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.StringFormat))
            {
                string videoPath = e.Data.GetData(DataFormats.StringFormat) as string;
                if (!string.IsNullOrEmpty(videoPath) && File.Exists(videoPath))
            {
                    try
                    {
                        // 드롭된 위치 계산
                        Point dropPosition = e.GetPosition(TimelineClipsCanvas);
                        double dropTimePosition = dropPosition.X / _pixelsPerSecond;
                        
                        // 드롭된 트랙 인덱스 계산
                        int trackIndex = (int)(dropPosition.Y / 60); // TrackToPositionConverter의 TRACK_HEIGHT와 동일
                        trackIndex = Math.Clamp(trackIndex, 0, 4); // 0-4 사이로 제한
                        
                        // 새 클립 추가
                        double duration = GetVideoDuration(videoPath);
                        
                        StatusTextBlock.Text = $"비디오 추가 중... ({Path.GetFileName(videoPath)})";
                        
                        // 썸네일 추출 진행
                        Progress<string> progress = new Progress<string>(status => 
                        {
                            StatusTextBlock.Text = status;
                        });
                        
                        // 취소 토큰
                        _cts?.Cancel();
                        _cts = new CancellationTokenSource();
                        
                        try
                        {
                            Directory.CreateDirectory(_thumbnailOutputDir);

                            string fileName = Path.GetFileName(videoPath);
                            string outputPath = Path.Combine(_thumbnailOutputDir, $"thumb_{DateTime.Now.Ticks}_{Path.GetFileNameWithoutExtension(videoPath)}.jpg");
                            
                            await ExtractThumbnailsAsync(videoPath, _thumbnailOutputDir, 1, progress);
                            
                            // 썸네일 경로 생성
                            using (VideoCapture capture = new VideoCapture(videoPath))
                {
                                int frameCount = (int)capture.Get(CapProp.FrameCount);
                                if (frameCount > 0)
                                {
                                    // 비디오 중간 프레임 가져오기
                                    capture.Set(CapProp.PosFrames, frameCount / 2);
                                    Mat frame = new Mat();
                                    bool success = capture.Read(frame);
                                    
                                    if (success)
                                    {
                                        CvInvoke.Imwrite(outputPath, frame);
                                        
                                        BitmapImage thumbImage = new BitmapImage();
                                        thumbImage.BeginInit();
                                        thumbImage.CacheOption = BitmapCacheOption.OnLoad;
                                        thumbImage.UriSource = new Uri(outputPath);
                                        thumbImage.EndInit();
                                        thumbImage.Freeze();
                    
                                        // 클립 생성
                                        VideoClip clip = new VideoClip
                                        {
                                            Name = Path.GetFileNameWithoutExtension(videoPath),
                                            VideoPath = videoPath,
                                            StartPosition = dropTimePosition,
                                            StartTime = 0,
                                            Duration = duration,
                                            Width = duration * _pixelsPerSecond,
                                            Thumbnail = thumbImage,
                                            TrackIndex = trackIndex
                                        };
                                        
                                        _mainViewModel.VideoEditor.TimelineClips.Add(clip);
                                        _selectedClip = clip;

                                        StatusTextBlock.Text = $"{fileName} 추가됨 - 길이: {TimeSpan.FromSeconds(duration):mm\\:ss}";
                                    }
                                }
                    }
                }
                catch (Exception ex)
                {
                            StatusTextBlock.Text = $"썸네일 생성 오류: {ex.Message}";
                }
            }
            catch (Exception ex)
            {
                        StatusTextBlock.Text = $"오류: {ex.Message}";
                    }
                }
            }
        }
        #endregion

        #region 비디오 처리 함수
        private void SetCurrentVideoPath(string videoPath)
        {
            if (!string.IsNullOrEmpty(videoPath) && File.Exists(videoPath))
            {
                _currentVideoPath = videoPath;
            }
        }

        public double GetVideoDuration(string videoPath)
        {
            if (string.IsNullOrEmpty(videoPath) || !File.Exists(videoPath))
            {
                MessageBox.Show($"비디오 파일을 찾을 수 없습니다: {videoPath}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
                return 0;
            }

            try
            {
                if (!File.Exists(_ffprobePath))
                {
                    MessageBox.Show("ffprobe를 찾을 수 없습니다. ffprobe가 설치되어 있는지 확인하세요.", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
                    return 0;
                }

                string args = $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{videoPath}\"";

                var processInfo = new ProcessStartInfo
                {
                    FileName = _ffprobePath,
                    Arguments = args,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using (var process = Process.Start(processInfo))
                {
                    if (process == null) return 0;

                    string output = process.StandardOutput.ReadToEnd();
                    string error = process.StandardError.ReadToEnd();

                    process.WaitForExit();

                    if (!string.IsNullOrEmpty(error))
                    {
                        MessageBox.Show($"ffprobe 오류: {error}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
                        return 0;
                    }

                    if (double.TryParse(output, out double duration))
                        return duration;
                    else
                    {
                        MessageBox.Show($"비디오 길이를 파싱할 수 없습니다: {output}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
                        return 0;
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"비디오 길이 확인 중 오류: {ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
                return 0;
            }
        }

        private async Task ExtractThumbnailsAsync(string videoPath, string outputDir, int count, IProgress<string> progress)
        {
            if (string.IsNullOrEmpty(videoPath) || !File.Exists(videoPath))
                throw new FileNotFoundException("비디오 파일을 찾을 수 없습니다.", videoPath);

            if (!File.Exists(_ffmpegPath))
                throw new FileNotFoundException("ffmpeg를 찾을 수 없습니다.", _ffmpegPath);

            // 썸네일 디렉토리 생성
            EnsureDirectoryExists(outputDir);

            // 비디오 길이 구하기
            double duration = GetVideoDuration(videoPath);
            if (duration <= 0)
                throw new InvalidOperationException("비디오 길이를 확인할 수 없습니다.");

            // 기존 썸네일 초기화
            Application.Current.Dispatcher.Invoke(() => {
                _mainViewModel.VideoEditor.Thumbnails.Clear();
                
                // ThumbnailItemsControl의 너비 설정 (표시 영역의 길이에 맞춤)
                if (ThumbnailItemsControl != null)
                {
                    double totalTimelineWidth = Math.Max(duration, DEFAULT_TIMELINE_SECONDS) * _pixelsPerSecond;
                    ThumbnailItemsControl.Width = totalTimelineWidth;
                }
            });

            try
            {
                double interval = duration / count;

                // 썸네일 추출
                for (int i = 0; i < count; i++)
                {
                    double timePosition = i * interval;
                    TimeSpan timestamp = TimeSpan.FromSeconds(timePosition);
                    string outputImagePath = Path.Combine(outputDir, $"thumb_{Path.GetFileNameWithoutExtension(videoPath)}_{i + 1}.jpg");

                    progress?.Report($"썸네일 추출 중: {i + 1}/{count}");

                    // 하드웨어 가속을 사용하는 ffmpeg 명령어로 수정
                    // NVIDIA GPU용 하드웨어 가속 옵션 추가: -hwaccel cuda -c:v h264_cuvid
                    // 하드웨어 인코더 사용: -c:v nvenc
                    var ffmpegProcessInfo = new ProcessStartInfo
                    {
                        FileName = _ffmpegPath,
                        Arguments = $"-hwaccel auto -ss {timestamp} -i \"{videoPath}\" -frames:v 1 -q:v 2 \"{outputImagePath}\" -y",
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardError = true
                    };

                    using (var process = Process.Start(ffmpegProcessInfo))
                    {
                        if (process == null) continue;

                        string error = await process.StandardError.ReadToEndAsync();
                        await process.WaitForExitAsync();

                        if (process.ExitCode != 0)
                        {
                            System.Diagnostics.Debug.WriteLine($"ffmpeg 오류: {error}");
                        }
                    }

                    if (File.Exists(outputImagePath))
                    {
                        try
                        {
                            var bitmap = new BitmapImage();
                            bitmap.BeginInit();
                            bitmap.CacheOption = BitmapCacheOption.OnLoad;
                            bitmap.UriSource = new Uri(outputImagePath);
                            bitmap.EndInit();
                            bitmap.Freeze();

                            // UI 스레드에서 Thumbnails 컬렉션 업데이트
                            Application.Current.Dispatcher.Invoke(() => {
                                _mainViewModel.VideoEditor.Thumbnails.Add(new ThumbnailItem
                                {
                                    Image = bitmap,
                                    ImagePath = outputImagePath,
                                    TimePosition = timePosition
                                });
                            });
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"썸네일 이미지 로드 오류: {ex.Message}");
                        }
                    }
                }

                // 마지막 업데이트 후 UI 갱신 확인
                Application.Current.Dispatcher.Invoke(() => {
                    if (ThumbnailItemsControl != null)
                    {
                        // ItemsSource 재할당해 바인딩 갱신 (필요한 경우)
                        var thumbnails = _mainViewModel.VideoEditor.Thumbnails;
                        ThumbnailItemsControl.ItemsSource = null;
                        ThumbnailItemsControl.ItemsSource = thumbnails;
                    }
                });

                progress?.Report("썸네일 생성 완료");
            }
            catch (Exception ex)
            {
                progress?.Report("썸네일 생성 실패");
                throw new Exception($"썸네일 추출 중 오류 발생: {ex.Message}", ex);
            }
        }

        // 비트맵 변환 함수
        private BitmapSource ToBitmapSource(Mat mat)
        {
            if (mat == null || mat.IsEmpty) return null;

            using (var bitmap = mat.ToBitmap())
            {
                IntPtr hBitmap = bitmap.GetHbitmap();
                try
                {
                    return Imaging.CreateBitmapSourceFromHBitmap(
                        hBitmap,
                        IntPtr.Zero,
                        Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                }
                finally
                {
                    DeleteObject(hBitmap);
                }
            }
        }

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteObject(IntPtr hObject);
        #endregion

        #region 이벤트 핸들러
        private void btnSelectVideo_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog
            {
                Filter = "비디오 파일|*.mp4;*.avi;*.mov;*.wmv;*.mkv|모든 파일|*.*",
                Multiselect = true
            };

            if (openFileDialog.ShowDialog() == true)
            {
                foreach (string videoPath in openFileDialog.FileNames)
                {
                    try
                    {
                        if (!File.Exists(videoPath))
                        {
                            MessageBox.Show($"파일을 찾을 수 없습니다: {videoPath}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
                            continue;
                        }

                        string fileName = Path.GetFileName(videoPath);
                        if (string.IsNullOrEmpty(fileName))
                        {
                            MessageBox.Show("파일명이 유효하지 않습니다.", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
                            continue;
                        }

                        _mainViewModel.VideoList.AddVideo(fileName, videoPath);
                        SetCurrentVideoPath(videoPath);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"비디오 파일을 열 수 없습니다: {ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
        }

        // 미디어가 로드된 후(오디오/비디오 길이 알 수 있음)
        private void MediaPlayer_LengthChanged(object sender, MediaPlayerLengthChangedEventArgs e)
        {
            if (e.Length <= 0) return;

            // e.Length는 밀리초 단위(long)
            double totalSec = e.Length / 1000.0;
            _currentVideoLengthSec = totalSec;

            // UI 스레드에서 실행 필요
            Dispatcher.Invoke(() =>
            {
                if (sliderSeekBar != null)
            {
                sliderSeekBar.Maximum = totalSec;
                }

                if (txtTotalTime != null)
                {
                    txtTotalTime.Text = FormatTime(totalSec);
                }

                DrawTimelineRuler(); // 영상 길이 바뀔 때마다 눈금자 갱신
            });
        }

        // 재생/일시정지 버튼
        private void btnPlayPause_Click(object sender, RoutedEventArgs e)
        {
            if (_mediaPlayer == null) return;

            if (_isPlaying)
            {
                _mediaPlayer.Pause();
                _isPlaying = false;
                btnPlayPause.Content = "▶";
                _timer?.Stop();

                if (_isRendering)
                {
                    CompositionTarget.Rendering -= OnRendering;
                    _isRendering = false;
                }
            }
            else
            {
                _mediaPlayer.Play();
                _isPlaying = true;
                btnPlayPause.Content = "❚❚";
                _timer?.Start();

                if (!_isRendering)
                {
                    CompositionTarget.Rendering += OnRendering;
                    _isRendering = true;
                }
            }
        }

        private void OnRendering(object sender, EventArgs e)
        {
            if (_mediaPlayer == null || !_isPlaying) return;

            double currentSec = _mediaPlayer.Time / 1000.0;
            double maxX = _currentVideoLengthSec * _pixelsPerSecond;

            // X 좌표 제한 (0 ~ 최대 길이)
            double x = Math.Clamp(currentSec * _pixelsPerSecond, 0, maxX);

            _playheadLine.X1 = x;
            _playheadLine.X2 = x;

            if (sliderSeekBar != null)
            {
            sliderSeekBar.Value = currentSec;
            }

            if (txtCurrentTime != null)
            {
            txtCurrentTime.Text = FormatTime(currentSec);
            }
        }

        // 정지 버튼
        private void btnStop_Click(object sender, RoutedEventArgs e)
        {
            if (_mediaPlayer == null) return;
            StopPlayback();
        }

        // 정지 로직
        private void StopPlayback()
        {
            _mediaPlayer.Stop();
            
            if (sliderSeekBar != null)
            {
            sliderSeekBar.Value = 0;
            }
            
            if (txtCurrentTime != null)
            {
            txtCurrentTime.Text = "00:00:00";
            }
            
            _isPlaying = false;
            
            if (btnPlayPause != null)
            {
            btnPlayPause.Content = "▶";
            }

            // 플레이헤드 위치 초기화
            if (_playheadLine != null)
            {
            _playheadLine.X1 = 0;
            _playheadLine.X2 = 0;
            }

            // 렌더링 이벤트 해제
            if (_isRendering)
            {
                CompositionTarget.Rendering -= OnRendering;
                _isRendering = false;
            }
            
            // 배속 초기화
            ResetPlaybackSpeed();
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            if (_mediaPlayer == null || !_isPlaying || _isSeeking) return;

            double currentSec = _mediaPlayer.Time / 1000.0;

            if (sliderSeekBar != null)
            {
            sliderSeekBar.Value = currentSec;
            }

            if (txtCurrentTime != null)
            {
            txtCurrentTime.Text = FormatTime(currentSec);
            }

            // 플레이헤드 위치 업데이트
            if (_playheadLine != null)
            {
            double currentX = currentSec * _pixelsPerSecond;
            _playheadLine.X1 = currentX;
            _playheadLine.X2 = currentX;
            }
        }

        // 슬라이더 이벤트
        private void sliderSeekBar_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            _isSeeking = true;
        }

        private void sliderSeekBar_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            _isSeeking = false;
            if (_mediaPlayer != null && sliderSeekBar != null)
            {
                _mediaPlayer.Time = (long)(sliderSeekBar.Value * 1000);
            }
        }

        private void sliderSeekBar_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_mediaPlayer == null || !_isSeeking) return;

                _mediaPlayer.Time = (long)(sliderSeekBar.Value * 1000);

            if (txtCurrentTime != null)
            {
                txtCurrentTime.Text = FormatTime(sliderSeekBar.Value);
            }
        }

        private void sliderVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_mediaPlayer != null && sliderVolume != null)
            {
                _mediaPlayer.Volume = (int)(sliderVolume.Value * 100);
            }
        }

        // 영상 편집 창 열기
        private void OpenTrimWindow_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentVideoPath) && _mediaPlayer?.Media != null)
            {
                SetCurrentVideoPath(_mediaPlayer.Media.Mrl);
            }

            if (string.IsNullOrEmpty(_currentVideoPath))
            {
                MessageBox.Show("먼저 영상을 선택하거나 재생해야 합니다.", "오류");
                return;
            }

            TrimVideoWindow trimWindow = new TrimVideoWindow();
            trimWindow.Owner = this;
            trimWindow.ShowDialog();
        }

        // 드래그 앤 드롭 관련
        private void VideoList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is ListBox listBox)
            {
                _selectedVideo = listBox.SelectedItem as MyVideo;
                
                // 현재 마우스 위치 저장
                Point position = e.GetPosition(null);
                listBox.Tag = position;
            }
        }

        private void VideoList_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && _selectedVideo != null)
            {
                DataObject data = new DataObject(DataFormats.StringFormat, _selectedVideo.FullPath);
                DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy);
            }
        }

        private void UpdateVideoInfo(MyVideo video)
        {
            _selectedVideo = video;
            
            if (_selectedVideo != null)
            {
                // 영상 정보 표시
                txtFileName.Text = _selectedVideo.name;
                txtFilePath.Text = _selectedVideo.FullPath;
                
                // 영상 길이 표시
                double duration = GetVideoDuration(_selectedVideo.FullPath);
                txtDuration.Text = FormatTime(duration);
                
                // 카테고리 선택 업데이트
                string category = _selectedVideo.Category;

                if (cmbCategory.Items.Cast<ComboBoxItem>().Any(item => item.Content.ToString() == category))
                {
                    cmbCategory.SelectedItem = cmbCategory.Items.Cast<ComboBoxItem>()
                        .First(item => item.Content.ToString() == category);
                }
                else if (!string.IsNullOrEmpty(category))
                {
                    cmbCategory.Text = category;
                }
                else
                {
                    cmbCategory.Text = "미분류";
                }
                
                // 비디오 정보 패널 표시
                VideoInfoPanel.Visibility = Visibility.Visible;
            }
        }
        
        private void CategoryComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
            {
            if (_selectedVideo == null || cmbCategory.SelectedItem == null) return;
            
            if (cmbCategory.SelectedItem is ComboBoxItem item)
            {
                _selectedVideo.Category = item.Content.ToString();
            }
        }
        
        private void CategoryComboBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                AddCategory(cmbCategory.Text);
                
                if (_selectedVideo != null)
                {
                    _selectedVideo.Category = cmbCategory.Text;
                }
                
                e.Handled = true;
            }
        }
        
        private void AddCategory_Click(object sender, RoutedEventArgs e)
        {
            AddCategory(cmbCategory.Text);
        }
        
        private void AddCategory(string category)
        {
            if (string.IsNullOrWhiteSpace(category)) return;
            
            // 메인뷰모델에 카테고리 추가
            _mainViewModel.AddCategory(category);
            
            // 콤보박스에 추가
            if (!cmbCategory.Items.Cast<ComboBoxItem>().Any(item => item.Content.ToString() == category))
            {
                ComboBoxItem newItem = new ComboBoxItem { Content = category };
                cmbCategory.Items.Add(newItem);
                cmbCategory.SelectedItem = newItem;
            }
            
            // 선택된 비디오에 카테고리 설정
            if (_selectedVideo != null)
            {
                _selectedVideo.Category = category;
            }
        }

        private void SpeedMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 컨트롤 참조가 없으면 다시 초기화 시도
                if (_speedControlPanel == null)
                {
                    InitializeControlReferences();
                    if (_speedControlPanel == null)
                    {
                        MessageBox.Show("배속 컨트롤을 찾을 수 없습니다.", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }
                }

                // 배속 패널 토글
                if (_speedControlPanel.Visibility == Visibility.Visible)
                {
                    _speedControlPanel.Visibility = Visibility.Collapsed;
                }
                else
                {
                    _speedControlPanel.Visibility = Visibility.Visible;
                    // 현재 배속값으로 UI 업데이트
                    if (_mediaPlayer != null)
                    {
                        UpdateSpeedUI(_mediaPlayer.Rate);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"배속 메뉴 오류: {ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SpeedPreset_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is Button button && button.Tag is string speedText)
                {
                    if (float.TryParse(speedText, out float speed))
                    {
                        SetPlaybackSpeed(speed);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"배속 프리셋 오류: {ex.Message}");
            }
        }

        private void SpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            try
            {
                if (_speedSlider == null || _speedValueText == null || _updatingUI) return;
                
                float speed = (float)_speedSlider.Value;
                _speedValueText.Text = $"{speed:F2}x";
                
                // 슬라이더 값이 변경될 때 바로 배속 적용
                SetPlaybackSpeed(speed);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"배속 슬라이더 오류: {ex.Message}");
            }
        }

        private void SpeedTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            try
            {
                if (e.Key == Key.Enter)
                {
                    ApplySpeedFromTextBox();
                    e.Handled = true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"배속 텍스트박스 오류: {ex.Message}");
            }
        }

        private void ApplySpeedButton_Click(object sender, RoutedEventArgs e)
        {
            ApplySpeedFromTextBox();
        }

        private void ApplySpeedFromTextBox()
        {
            try
            {
                if (_speedTextBox == null) return;
                
                if (float.TryParse(_speedTextBox.Text, out float speed))
                {
                    // 유효한 범위로 제한 (0.25 ~ 3.0)
                    speed = Math.Clamp(speed, 0.25f, 3.0f);
                    SetPlaybackSpeed(speed);
                }
                else
                {
                    // 잘못된 입력이면 현재 배속으로 리셋
                    if (_mediaPlayer != null)
                    {
                        UpdateSpeedUI(_mediaPlayer.Rate);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"배속 텍스트박스 적용 오류: {ex.Message}");
            }
        }

        private void SetPlaybackSpeed(float speed)
        {
            if (_mediaPlayer == null) return;
            
            try
            {
                // LibVLC MediaPlayer의 재생 속도 설정 방법을 사용
                _mediaPlayer.SetRate(speed);
                UpdateSpeedUI(speed);
                
                if (StatusTextBlock != null)
                {
                    StatusTextBlock.Text = $"배속이 {speed:F2}x로 변경되었습니다.";
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"배속 변경 중 오류가 발생했습니다: {ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void UpdateSpeedUI(float speed)
        {
            try
            {
                if (_speedSlider == null || _speedTextBox == null || _speedValueText == null) return;
                
                _updatingUI = true;
                try
                {
                    _speedSlider.Value = speed;
                    _speedTextBox.Text = speed.ToString("F2");
                    _speedValueText.Text = $"{speed:F2}x";
                }
                finally
                {
                    _updatingUI = false;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"배속 UI 업데이트 오류: {ex.Message}");
            }
        }

        // 배속 초기화 (1.0)
        private void ResetPlaybackSpeed()
        {
            if (_mediaPlayer == null) return;
            
            try
            {
                _mediaPlayer.SetRate(1.0f);
                
                // UI가 표시 중이면 UI도 업데이트
                if (_speedControlPanel != null && _speedControlPanel.Visibility == Visibility.Visible)
                {
                    UpdateSpeedUI(1.0f);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"배속 초기화 중 오류: {ex.Message}");
            }
        }

        private void VideoList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ListBox listBox && listBox.SelectedItem is MyVideo video)
            {
                UpdateVideoInfo(video);
            }
        }

        private async void btnExportVideo_Click(object sender, RoutedEventArgs e)
        {
            // 타임라인에 클립이 없는 경우
            if (_mainViewModel.VideoEditor.TimelineClips.Count == 0)
            {
                MessageBox.Show("내보낼 클립이 없습니다. 먼저 영상을 타임라인에 추가해주세요.", "내보내기 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 저장 파일 경로 선택
            SaveFileDialog saveDialog = new SaveFileDialog
            {
                Title = "내보낼 파일 위치 선택",
                Filter = "MP4 파일 (*.mp4)|*.mp4",
                DefaultExt = ".mp4",
                FileName = "output.mp4"
            };

            if (saveDialog.ShowDialog() != true)
            {
                return;
            }

            string outputPath = saveDialog.FileName;

            try
            {
                // FFmpeg이 설치되어 있는지 확인
                if (!File.Exists(_ffmpegPath))
                {
                    MessageBox.Show("FFmpeg을 찾을 수 없습니다. FFmpeg 설치 경로를 확인해주세요.", "내보내기 오류", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                StatusTextBlock.Text = "영상 내보내기 준비 중...";

                // 클립 목록 정렬 (시작 위치 기준)
                var orderedClips = _mainViewModel.VideoEditor.TimelineClips
                    .OrderBy(c => c.StartPosition)
                    .ToList();

                // 임시 파일 목록
                List<string> tempFiles = new List<string>();
                
                // 임시 디렉토리 생성
                string tempDir = Path.Combine(Path.GetTempPath(), "VideoEditor_" + Guid.NewGuid().ToString().Substring(0, 8));
                Directory.CreateDirectory(tempDir);

                try
                {
                    int clipIndex = 0;
                _cts?.Dispose();
                _cts = new CancellationTokenSource();

                    foreach (var clip in orderedClips)
                    {
                        string inputPath = clip.VideoPath;
                        if (string.IsNullOrEmpty(inputPath) || !File.Exists(inputPath))
                        {
                            MessageBox.Show($"클립 '{clip.Name}'의 원본 파일을 찾을 수 없습니다.", "내보내기 오류", MessageBoxButton.OK, MessageBoxImage.Error);
                            continue;
                        }

                        // 클립 추출 임시 파일
                        string tempOutput = Path.Combine(tempDir, $"clip_{clipIndex}.mp4");
                        tempFiles.Add(tempOutput);

                        StatusTextBlock.Text = $"클립 {clipIndex + 1}/{orderedClips.Count} 처리 중...";

                        // FFmpeg 명령어로 클립 추출
                        string args = $"-i \"{inputPath}\" -ss {clip.StartTime} -t {clip.Duration} -c:v h264_nvenc -preset fast -c:a aac \"{tempOutput}\"";

                        var processInfo = new ProcessStartInfo
                        {
                            FileName = _ffmpegPath,
                            Arguments = args,
                            CreateNoWindow = true,
                            UseShellExecute = false,
                            RedirectStandardError = true,
                            RedirectStandardOutput = true
                        };

                        using (Process process = Process.Start(processInfo))
                        {
                            if (process == null)
                            {
                                MessageBox.Show("FFmpeg 프로세스를 시작할 수 없습니다.", "내보내기 오류", MessageBoxButton.OK, MessageBoxImage.Error);
                                return;
                            }

                            // 비동기로 프로세스 완료 대기
                            await process.WaitForExitAsync(_cts.Token);

                            if (process.ExitCode != 0)
                            {
                                string error = await process.StandardError.ReadToEndAsync();
                                MessageBox.Show($"클립 추출 중 오류 발생: {error}", "내보내기 오류", MessageBoxButton.OK, MessageBoxImage.Error);
                                return;
                            }
                        }

                        clipIndex++;
                    }

                    // 텍스트 파일 생성 (클립 목록)
                    string listFile = Path.Combine(tempDir, "filelist.txt");
                    using (StreamWriter writer = new StreamWriter(listFile))
                    {
                        foreach (var file in tempFiles)
                        {
                            if (File.Exists(file))
                            {
                                writer.WriteLine($"file '{file.Replace("'", "\\'")}'");
                            }
                        }
                    }

                    StatusTextBlock.Text = "최종 영상 생성 중...";

                    // 클립 이어붙이기
                    string concatArgs = $"-f concat -safe 0 -i \"{listFile}\" -c copy \"{outputPath}\"";
                    var concatProcess = new ProcessStartInfo
                    {
                        FileName = _ffmpegPath,
                        Arguments = concatArgs,
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardError = true
                    };

                    using (Process process = Process.Start(concatProcess))
                    {
                        if (process == null)
                        {
                            MessageBox.Show("FFmpeg 프로세스를 시작할 수 없습니다.", "내보내기 오류", MessageBoxButton.OK, MessageBoxImage.Error);
                            return;
                        }

                        await process.WaitForExitAsync(_cts.Token);

                        if (process.ExitCode != 0)
                        {
                            string error = await process.StandardError.ReadToEndAsync();
                            MessageBox.Show($"영상 이어붙이기 중 오류 발생: {error}", "내보내기 오류", MessageBoxButton.OK, MessageBoxImage.Error);
                            return;
                        }
                    }

                    StatusTextBlock.Text = "영상 내보내기 완료!";
                    MessageBox.Show($"영상이 성공적으로 내보내졌습니다.\n저장 위치: {outputPath}", "내보내기 완료", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (OperationCanceledException)
            {
                    StatusTextBlock.Text = "내보내기 취소됨";
                    MessageBox.Show("사용자에 의해 작업이 취소되었습니다.", "내보내기 취소", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                    StatusTextBlock.Text = "내보내기 실패";
                    MessageBox.Show($"영상 내보내기 중 예기치 않은 오류 발생: {ex.Message}", "내보내기 오류", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                    // 임시 파일 정리
                    try
                    {
                        foreach (var file in tempFiles)
                        {
                            if (File.Exists(file))
                            {
                                File.Delete(file);
                            }
                        }

                        if (Directory.Exists(tempDir))
                        {
                            Directory.Delete(tempDir, true);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"임시 파일 정리 중 오류: {ex.Message}");
                }

                _cts?.Dispose();
                _cts = null;
                }
            }
            catch (Exception ex)
            {
                StatusTextBlock.Text = "내보내기 실패";
                MessageBox.Show($"내보내기 중 예기치 않은 오류 발생: {ex.Message}", "내보내기 오류", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void btnCopyClip_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedClip == null)
            {
                MessageBox.Show("복사할 클립을 먼저 선택해주세요.", "알림");
                return;
            }
            
            _copiedClip = _selectedClip.Clone();
            StatusTextBlock.Text = $"클립 '{_copiedClip.Name}'이(가) 복사되었습니다.";
        }
        
        private void btnPasteClip_Click(object sender, RoutedEventArgs e)
        {
            if (_copiedClip == null)
            {
                MessageBox.Show("붙여넣을 클립이 없습니다. 먼저 클립을 복사하세요.", "알림");
                return;
            }
            
            try
            {
                // 복사본 만들기
                VideoClip newClip = _copiedClip.Clone();
                
                // 복사한 클립 위치 조정 (약간 오프셋)
                newClip.StartPosition += 1.0;
                
                // 현재 선택된 클립의 트랙 인덱스와 같게 설정하거나, 적절한 빈 트랙 찾기
                if (_selectedClip != null)
                {
                    newClip.TrackIndex = _selectedClip.TrackIndex;
                }
                
                _mainViewModel.VideoEditor.TimelineClips.Add(newClip);
                _selectedClip = newClip;
                
                StatusTextBlock.Text = $"클립이 복사되었습니다: {newClip.Name}";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"클립 붙여넣기 오류: {ex.Message}", "오류");
            }
        }
        
        private void btnDeleteClip_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedClip == null)
            {
                MessageBox.Show("삭제할 클립을 먼저 선택해주세요.", "알림");
                return;
            }
            
            var result = MessageBox.Show($"클립 '{_selectedClip.Name}'을(를) 삭제하시겠습니까?", 
                "클립 삭제", MessageBoxButton.YesNo, MessageBoxImage.Question);
                
            if (result == MessageBoxResult.Yes)
            {
                _mainViewModel.VideoEditor.TimelineClips.Remove(_selectedClip);
                _selectedClip = null;
                StatusTextBlock.Text = "클립이 삭제되었습니다.";
            }
        }

        #region 유틸리티 함수
        // 시간(초)을 "hh:mm:ss" 형태로 변환
        private string FormatTime(double totalSeconds)
        {
            return TimeSpan.FromSeconds(totalSeconds).ToString(@"hh\:mm\:ss");
        }

        // 영상 자르기 실행
        public async void TrimVideoFromUI(TimeSpan startTime, TimeSpan endTime)
        {
            if (string.IsNullOrEmpty(_currentVideoPath))
            {
                MessageBox.Show("먼저 영상을 선택해주세요.", "오류");
                return;
            }

            if (!File.Exists(_ffmpegPath))
            {
                MessageBox.Show("ffmpeg를 찾을 수 없습니다. ffmpeg가 설치되어 있는지 확인하세요.", "오류");
                return;
            }

            try
            {
                string outputFile = Path.Combine(
                    Path.GetDirectoryName(_currentVideoPath),
                    $"trimmed_{Path.GetFileName(_currentVideoPath)}");

                if (CutVideoButton != null)
                {
                CutVideoButton.IsEnabled = false;
                }

                _cts?.Dispose();
                _cts = new CancellationTokenSource();
                var progress = new Progress<string>(s =>
                {
                    if (StatusTextBlock != null)
                    {
                        StatusTextBlock.Text = s;
                    }
                });

                await Task.Run(() => _editFunction.TrimVideo(_currentVideoPath, outputFile, startTime, endTime, progress, _cts.Token));

                MessageBox.Show("영상 자르기가 완료되었습니다!", "성공");
            }
            catch (OperationCanceledException)
            {
                MessageBox.Show("작업이 취소되었습니다.", "취소");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"오류: {ex.Message}", "오류");
            }
            finally
            {
                if (CutVideoButton != null)
            {
                CutVideoButton.IsEnabled = true;
                }

                _cts?.Dispose();
                _cts = null;
            }
        }

        private void PlayClipFromPosition(VideoClip clip)
        {
            try
            {
                if (_mediaPlayer == null || string.IsNullOrEmpty(clip.VideoPath) || !File.Exists(clip.VideoPath))
                    return;
                
                // 현재 재생 중인 파일이 다르면 새 미디어로 로드
                if (_currentVideoPath != clip.VideoPath)
                {
                    var media = new Media(_libVLC, clip.VideoPath, FromType.FromPath);
                    _mediaPlayer.Media = media;
                    media.Dispose();
                    SetCurrentVideoPath(clip.VideoPath);
                }
                
                // 클립의 시작 위치(초)로 이동하여 재생
                _mediaPlayer.Time = (long)((clip.StartPosition + clip.StartTime) * 1000);
                _mediaPlayer.Play();
                _isPlaying = true;
                
                if (btnPlayPause != null)
                {
                    btnPlayPause.Content = "❚❚";
                }
                
                if (show_VideoBar != null)
                {
                    show_VideoBar.Visibility = Visibility.Visible;
                }
                
                // 타이머 시작
                if (_timer != null)
                {
                    _timer.Start();
                }
                
                // 렌더링 이벤트 설정
                if (!_isRendering)
                {
                    CompositionTarget.Rendering += OnRendering;
                    _isRendering = true;
                }
                
                StatusTextBlock.Text = $"클립 '{clip.Name}' 재생 중";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"클립 재생 중 오류 발생: {ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        #endregion

        // 창 종료 시 자원 정리
        protected override void OnClosed(EventArgs e)
        {
            CleanupResources();
            base.OnClosed(e);
        }

        private void CleanupResources()
        {
            // 렌더링 이벤트 해제
            if (_isRendering)
            {
                CompositionTarget.Rendering -= OnRendering;
                _isRendering = false;
            }

            // 타이머 정리
            if (_timer != null)
            {
                _timer.Stop();
                _timer.Tick -= Timer_Tick;
                _timer = null;
            }

            // 미디어 플레이어 정리
            if (_mediaPlayer != null)
            {
                _mediaPlayer.LengthChanged -= MediaPlayer_LengthChanged;
                _mediaPlayer.Stop();
                _mediaPlayer.Dispose();
                _mediaPlayer = null;
            }

            // LibVLC 정리
            _libVLC?.Dispose();
            _libVLC = null;

            // 취소 토큰 정리
            _cts?.Dispose();
            _cts = null;
        }

        // 타임라인 확대/축소 기능 구현
        private void UpdateZoomLevel(double newZoomLevel)
        {
            // 줌 레벨 제한 (0.1 ~ 5.0)
            _zoomLevel = Math.Clamp(newZoomLevel, 0.1, 5.0);
            
            // 픽셀 단위 업데이트
            _pixelsPerSecond = 10 * _zoomLevel;
            
            // 줌 레벨 표시 업데이트
            if (txtZoomValue != null)
            {
                txtZoomValue.Text = $"{_zoomLevel * 100:0}%";
            }
            
            // 타임라인 다시 그리기
            DrawTimelineRuler();
            
            // 모든 클립 위치와 너비 업데이트
            UpdateAllClipWidths();
        }
        
        private void UpdateAllClipWidths()
        {
            if (_mainViewModel?.VideoEditor?.TimelineClips == null) return;
            
            foreach (var clip in _mainViewModel.VideoEditor.TimelineClips)
            {
                clip.Width = clip.Duration * _pixelsPerSecond;
            }
        }
        
        private void btnZoomIn_Click(object sender, RoutedEventArgs e)
        {
            UpdateZoomLevel(_zoomLevel * 1.2);
        }
        
        private void btnZoomOut_Click(object sender, RoutedEventArgs e)
        {
            UpdateZoomLevel(_zoomLevel / 1.2);
        }
        
        private void TimelineScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl))
            {
                // Ctrl + 마우스 휠로 줌 레벨 조정
                if (e.Delta > 0)
                {
                    UpdateZoomLevel(_zoomLevel * 1.1);
                }
                else
                {
                    UpdateZoomLevel(_zoomLevel / 1.1);
                }
                
                e.Handled = true;
            }
        }
        
        // 클립 조작 이벤트 처리
        private void Clip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is VideoClip clip)
        {
                _selectedClip = clip;
                _isDraggingClip = true;
                Point dragStartPoint = e.GetPosition(TimelineClipsCanvas);
                _originalClipStart = clip.StartPosition;
                
                // 드래그 시작 포인트를 태그로 저장
                element.Tag = dragStartPoint;
                
                element.CaptureMouse();
                e.Handled = true;
                
                // 더블 클릭 시 해당 클립의 시작 위치로 이동
                if (e.ClickCount == 2)
                {
                    PlayClipFromPosition(clip);
                }
            }
        }
        
        private void Clip_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isDraggingClip && _selectedClip != null && sender is FrameworkElement element)
            {
                // 이전에 저장된 드래그 시작 포인트 가져오기
                if (element.Tag is Point dragStartPoint)
                {
                    Point currentPoint = e.GetPosition(TimelineClipsCanvas);
                    double deltaX = currentPoint.X - dragStartPoint.X;
                    
                    // 초 단위로 변환
                    double deltaSeconds = deltaX / _pixelsPerSecond;
                    
                    // 새 위치 계산 (음수 방지)
                    double newPosition = Math.Max(0, _originalClipStart + deltaSeconds);
                    
                    // 클립 위치 업데이트
                    _selectedClip.StartPosition = newPosition;
                    
                    // 트랙 변경 처리 (Y축 이동)
                    int newTrackIndex = (int)(currentPoint.Y / 60); // TrackToPositionConverter의 TRACK_HEIGHT와 동일
                    newTrackIndex = Math.Clamp(newTrackIndex, 0, 4); // 0-4 사이로 제한
                    
                    if (_selectedClip.TrackIndex != newTrackIndex)
                    {
                        _selectedClip.TrackIndex = newTrackIndex;
                    }
                    
                    e.Handled = true;
                }
            }
        }
        
        private void Clip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDraggingClip && sender is FrameworkElement element)
            {
                _isDraggingClip = false;
                element.ReleaseMouseCapture();
                e.Handled = true;
        }
    }

        private void TimelineClipsCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // 캔버스 클릭 시 선택 해제
            _selectedClip = null;
        }
        
        private void btnSplitClip_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedClip == null)
            {
                MessageBox.Show("분할할 클립을 먼저 선택해주세요.", "알림");
                return;
            }
            
            // 재생 위치 가져오기
            double currentTime = _mediaPlayer?.Time / 1000.0 ?? 0;
            
            // 클립 시작/끝 시간 기준으로 변환
            double clipCurrentTime = _selectedClip.StartPosition;
            double relativePosition = currentTime - clipCurrentTime;
            
            // 클립 내 위치가 아니면 경고
            if (relativePosition <= 0 || relativePosition >= _selectedClip.Duration)
            {
                MessageBox.Show("재생 위치가 선택한 클립 내에 있어야 합니다.", "알림");
                return;
            }
            
            try
            {
                // 원본 클립 길이 조정
                double originalDuration = _selectedClip.Duration;
                _selectedClip.Duration = relativePosition;
                
                // 새 클립 생성
                VideoClip newClip = new VideoClip
                {
                    Name = _selectedClip.Name + " (분할)",
                    VideoPath = _selectedClip.VideoPath,
                    StartTime = _selectedClip.StartTime + relativePosition,
                    Duration = originalDuration - relativePosition,
                    StartPosition = _selectedClip.StartPosition + relativePosition,
                    Thumbnail = _selectedClip.Thumbnail,
                    Category = _selectedClip.Category
                };
                
                // 너비 업데이트
                _selectedClip.Width = _selectedClip.Duration * _pixelsPerSecond;
                newClip.Width = newClip.Duration * _pixelsPerSecond;
                
                // 새 클립 추가
                _mainViewModel.VideoEditor.TimelineClips.Add(newClip);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"클립 분할 중 오류 발생: {ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        }
        #endregion
    }
}