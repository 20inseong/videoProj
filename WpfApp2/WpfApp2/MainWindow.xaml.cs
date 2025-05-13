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

namespace WpfApp2
{
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
        private const double PIXELS_PER_SECOND = 10; // 1초당 10픽셀
        private double _currentVideoLengthSec = DEFAULT_TIMELINE_SECONDS;

        private Line _playheadLine;
        private string _ffmpegPath = @"C:\ffmpeg\bin\ffmpeg.exe";
        private string _ffprobePath = @"C:\ffmpeg\bin\ffprobe.exe";
        private string _thumbnailOutputDir;

        // XAML에 정의된 배속 컨트롤 참조
        private StackPanel _speedControlPanel;
        private TextBox _speedTextBox;
        private Slider _speedSlider;
        private TextBlock _speedValueText;

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
        // ... 기존 코드 유지 ...
        #endregion

        #region 타임라인 관련 함수
        private void DrawTimelineRuler()
        {
            if (TimelineRulerCanvas == null || ThumbnailItemsControl == null) return;

            // 영상이 없어도 항상 5분 기준으로 그림
            double videoLength = Math.Max(_currentVideoLengthSec, DEFAULT_TIMELINE_SECONDS);
            double totalTimelineWidth = videoLength * PIXELS_PER_SECOND;

            TimelineRulerCanvas.Children.Clear();
            TimelineRulerCanvas.Width = totalTimelineWidth;

            // 썸네일 StackPanel도 동일한 폭으로 맞춤
                ThumbnailItemsControl.Width = totalTimelineWidth;

            // 1초마다 얇은 선, 5초마다 굵은 선+숫자
            for (int sec = 0; sec <= videoLength; sec++)
            {
                double x = sec * PIXELS_PER_SECOND;
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
            // "MyVideo" 타입의 데이터가 드래그 중이면 복사 가능 효과 표시
            e.Effects = e.Data.GetDataPresent("MyVideo") ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private async void Timeline_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent("MyVideo")) return;
            
            var video = e.Data.GetData("MyVideo") as MyVideo;
            if (video == null || string.IsNullOrEmpty(video.FullPath)) 
            {
                MessageBox.Show("유효하지 않은 비디오 파일입니다.", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (!File.Exists(video.FullPath))
            {
                MessageBox.Show($"파일을 찾을 수 없습니다: {video.FullPath}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            try
            {
                StatusTextBlock.Text = "비디오 정보 불러오는 중...";

                // 비디오 길이 가져오기
                double videoLength = GetVideoDuration(video.FullPath);
                if (videoLength <= 0)
                {
                    MessageBox.Show("비디오 길이를 가져올 수 없습니다. 파일이 손상되었거나 지원되지 않는 형식일 수 있습니다.", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
                    StatusTextBlock.Text = "비디오 로드 실패";
                    return;
                }

                _currentVideoLengthSec = Math.Max(DEFAULT_TIMELINE_SECONDS, videoLength);

                // 눈금자 즉시 갱신
                DrawTimelineRuler();

                // 썸네일 디렉토리 확인
                EnsureDirectoryExists(_thumbnailOutputDir);

                // 썸네일 생성
                StatusTextBlock.Text = "썸네일 생성 중...";
                var progress = new Progress<string>(s => StatusTextBlock.Text = s);
                
                try
                {
                    await ExtractThumbnailsAsync(video.FullPath, _thumbnailOutputDir, 10, progress);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"썸네일 생성 실패: {ex.Message}\n비디오 재생은 계속 진행합니다.", "경고", MessageBoxButton.OK, MessageBoxImage.Warning);
                }

                // 영상 화면에 표시
                StatusTextBlock.Text = "비디오 로드 중...";
                try
                {
                    var media = new Media(_libVLC, new Uri(video.FullPath));
                    await media.Parse(MediaParseOptions.ParseLocal);
                    _mediaPlayer.Media = media;
                    _mediaPlayer.Pause();
                    
                    // 배속 초기화
                    ResetPlaybackSpeed();
                    
                    SetCurrentVideoPath(video.FullPath);

                    if (show_VideoBar != null)
                    {
                        show_VideoBar.Visibility = Visibility.Visible;
                    }

                    if (_timer == null)
                    {
                        _timer = new DispatcherTimer
                        {
                            Interval = TimeSpan.FromMilliseconds(33)
                        };
                        _timer.Tick += Timer_Tick;
                    }
                    _timer.Stop();

                    StatusTextBlock.Text = "비디오 로드 완료";
                    
                    // 타임라인 스크롤뷰 초기화 (맨 왼쪽으로 스크롤)
                    if (TimelineScrollViewer != null)
                    {
                        TimelineScrollViewer.ScrollToLeftEnd();
                    }
                    
                    // 플레이헤드 위치 초기화
                    if (_playheadLine != null)
                    {
                        _playheadLine.X1 = 0;
                        _playheadLine.X2 = 0;
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"비디오 로드 실패: {ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
                    StatusTextBlock.Text = "비디오 로드 실패";
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"비디오 로드 중 오류 발생: {ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
                StatusTextBlock.Text = "비디오 로드 실패";
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
                    double totalTimelineWidth = Math.Max(duration, DEFAULT_TIMELINE_SECONDS) * PIXELS_PER_SECOND;
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
            double maxX = _currentVideoLengthSec * PIXELS_PER_SECOND;

            // X 좌표 제한 (0 ~ 최대 길이)
            double x = Math.Clamp(currentSec * PIXELS_PER_SECOND, 0, maxX);

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
            double currentX = currentSec * PIXELS_PER_SECOND;
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
        private Point _dragStartPoint;

        private void VideoList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragStartPoint = e.GetPosition(null);
        }

        private void VideoList_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;

            Point mousePos = e.GetPosition(null);
            Vector diff = _dragStartPoint - mousePos;

            if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
            {
                if (sender is not ListBox listBox) return;

                var selectedVideo = listBox.SelectedItem as MyVideo;
                if (selectedVideo != null)
                {
                    DataObject dragData = new DataObject("MyVideo", selectedVideo);
                    DragDrop.DoDragDrop(listBox, dragData, DragDropEffects.Copy);
                }
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
        #endregion

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
    }

    public class MainViewModel
    {
        public MyVideoViewModel VideoList { get; }
        public VideoEditorViewModel VideoEditor { get; }

        public MainViewModel()
        {
            VideoList = new MyVideoViewModel();
            VideoEditor = new VideoEditorViewModel();
        }
    }

    public class MyVideo
    {
        public string name { get; set; }
        public string FullPath { get; set; }
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

        public VideoEditorViewModel()
        {
            Thumbnails = new ObservableCollection<ThumbnailItem>();
        }
    }
}