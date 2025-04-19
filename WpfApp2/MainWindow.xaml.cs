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
using System.Diagnostics;
using System.Windows.Media;

namespace WpfApp2
{
    public partial class MainWindow : Window
    {
        private DispatcherTimer _timer;
        private bool _isPlaying = false; // 재생 상태 파악 (Play/Pause 구분)
        private MainViewModel _mainViewModel;
        private EditFunction _editFunction = new EditFunction();
        private string _currentVideoPath;
        private CancellationTokenSource _cts;
        private LibVLC _libVLC;
        private LibVLCSharp.Shared.MediaPlayer _mediaPlayer;

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            HwndSource hwndSource = PresentationSource.FromVisual(this) as HwndSource;
            hwndSource.CompositionTarget.RenderMode = RenderMode.Default; // 가능하면 하드웨어 가속 사용

            // CacheMode 설정 추가 (기존 MediaElement 대신 VideoView를 사용하므로 MediaElement는 제거됨)
        }

        public MainWindow()
        {
            InitializeComponent();
            Core.Initialize();
            _libVLC = new LibVLC();
            _mediaPlayer = new LibVLCSharp.Shared.MediaPlayer(_libVLC);
            videoView.MediaPlayer = _mediaPlayer;

            // 이벤트 구독
            _mediaPlayer.LengthChanged += MediaPlayer_LengthChanged;


            CvInvoke.UseOpenCL = true; // OpenCL 활성화, GPU 가속 (편집/썸네일 생성용)

            _mainViewModel = new MainViewModel();
            this.DataContext = _mainViewModel;
        }

        // taskbar제외 화면 길이 구하기
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


        // Camera List + Img Slider List
        public class MainViewModel
        {
            public MyVideoViewModel VideoList { get; set; }
            public VideoEditorViewModel VideoEditor { get; set; }

            public MainViewModel()
            {
                VideoList = new MyVideoViewModel();
                VideoEditor = new VideoEditorViewModel();
            }
        }

        // Video Object Imformation
        public class MyVideo
        {
            public string name { get; set; }
            public string FullPath { get; set; } // 전체 파일 경로
        }

        // Video List Object
        public class MyVideoViewModel
        {
            public ObservableCollection<MyVideo> MyVideoes { get; set; }

            public MyVideoViewModel()
            {
                MyVideoes = new ObservableCollection<MyVideo>();
            }

            public void AddVideo(string fileName, string fullPath)
            {
                MyVideo videoItem = new MyVideo { name = fileName, FullPath = fullPath };
                MyVideoes.Add(videoItem);
            }

        }

        // 썸네일 위치와 시간 객체
        public class ThumbnailItem
        {
            public BitmapImage Image { get; set; }
            public double TimePosition { get; set; } // 초 단위로 저장
        }

        // Img Slider
        public class VideoEditorViewModel
        {
            public ObservableCollection<ThumbnailItem> Thumbnails { get; set; }

            public VideoEditorViewModel()
            {
                Thumbnails = new ObservableCollection<ThumbnailItem>(); // UI 자동 갱신을 위한 컬렉션
            }

            public void GenerateThumbnails(string videoPath)
            {
                Thumbnails.Clear(); // 기존 썸네일 초기화
                int maxThumbnails = 10; // 최대 10개의 썸네일 생성 (너무 많으면 성능 저하)

                using (var capture = new VideoCapture(videoPath))
                {
                    double fps = capture.Get(CapProp.Fps); // 초당 프레임 수
                    double totalFrames = capture.Get(CapProp.FrameCount);
                    double videoLength = totalFrames / fps; // 영상 길이(초)

                    if (videoLength <= 0 || fps <= 0)
                    {
                        MessageBox.Show("잘못된 비디오 파일입니다.");
                        return;
                    }

                    double interval = Math.Max(1, videoLength / maxThumbnails); // 썸네일 간격 계산

                    for (int i = 0; i < maxThumbnails; i++)
                    {
                        double timePosition = i * interval; // 해당 초 위치로 이동
                        capture.Set(CapProp.PosMsec, timePosition * 1000); // 특정 시간으로 이동

                        using (Mat frame = new Mat())
                        {
                            capture.Read(frame);
                            if (frame.IsEmpty)
                            {
                                continue; // 프레임을 읽지 못하면 건너뛰기
                            }

                            // 섬네일 추가
                            Thumbnails.Add(new ThumbnailItem
                            {
                                Image = ConvertMatToBitmapImage(frame),
                                TimePosition = timePosition
                            });
                        }
                    }
                }
            }

            private BitmapImage ConvertMatToBitmapImage(Mat mat)
            {
                using (var bitmap = mat.ToBitmap())
                {
                    MemoryStream stream = new MemoryStream();
                    bitmap.Save(stream, ImageFormat.Png);
                    stream.Position = 0; // 반드시 스트림의 시작점으로 이동

                    BitmapImage image = new BitmapImage();
                    image.BeginInit();
                    image.StreamSource = stream;
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.EndInit();
                    image.Freeze(); // UI에서 사용 가능하도록 Freeze 호출
                    return image;
                }
            }

        }

        // 선택한 영상 파일 경로 저장 함수
        private void SetCurrentVideoPath(string videoPath)
        {
            if (!string.IsNullOrEmpty(videoPath) && File.Exists(videoPath))
            {
                _currentVideoPath = videoPath;
            }
        }

        // 영상 재생과 관련된 함수들
        // (1) 영상 선택 버튼
        private async void btnSelectVideo_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog
            {
                Filter = "비디오 파일|*.mp4;*.avi;*.mov;*.wmv;*.mkv|모든 파일|*.*",
                Multiselect = true  // 여러 개의 파일 선택 가능
            };

            if (openFileDialog.ShowDialog() == true)
            {

                foreach (string videoPath in openFileDialog.FileNames) // 여러 개의 파일 처리
                {
                    try
                    {
                        try
                        {
                            // 파일명 추출
                            string fileName = System.IO.Path.GetFileName(openFileDialog.FileName);

                            // 파일명이 유효한지 확인
                            if (string.IsNullOrEmpty(fileName))
                            {
                                MessageBox.Show("파일을 선택하지 않았습니다.");
                                //txtFileName.Text = "Why so serious?";
                                continue;
                            }

                            // ViewModel이 null인지 확인
                            if (_mainViewModel.VideoList == null)
                            {
                                _mainViewModel.VideoList = new MyVideoViewModel(); // 초기화
                            }

                            // 썸네일 생성 및 VideoList에 추가
                            _mainViewModel.VideoList.AddVideo(fileName, videoPath);
                        }
                        catch (Exception ex)
                        {
                            // 예외 처리 (로그 기록 또는 사용자 알림)
                            MessageBox.Show($"오류가 발생했습니다: {ex.Message}");
                            continue;
                            //txtFileName.Text = "Error is coming";
                        }

                        //// LibVLCSharp로 영상 재생: Media 생성 시 FromType.FromPath 사용
                        //var media = new Media(_libVLC, videoPath, FromType.FromPath);
                        //await media.Parse(MediaParseOptions.ParseLocal); // ← 이걸 해야 Length 정보가 정확해짐
                        //_mediaPlayer.Media = media;
                        //_mediaPlayer.Play(); //play를 해줘야 영상길이를 알 수 있음.
                        //_mediaPlayer.Pause();
                        //Debug.WriteLine("영상 길이(ms): " + _mediaPlayer.Length);
                        //Debug.WriteLine("media.Duration(ms): " + media.Duration);

                        //_isPlaying = false;
                        //btnPlayPause.Content = "▶"; // 재생 아이콘
                        //_mediaPlayer.Volume = (int)(sliderVolume.Value * 100);
                        //show_VideoBar.Visibility = Visibility.Visible;

                        //// 비디오 길이 설정
                        //if (_mediaPlayer.Length > 0)
                        //{
                        //    double videoLength = _mediaPlayer.Length / 1000.0; // 밀리초를 초로 변환
                        //    sliderSeekBar.Maximum = videoLength;
                        //    Console.WriteLine($"Video Length: {videoLength}");
                        //    sliderSeekBar.Value = 0; // 슬라이더 초기화
                        //    txtTotalTime.Text = FormatTime(videoLength);
                        //    txtCurrentTime.Text = "00:00:00";
                        //}


                        SetCurrentVideoPath(videoPath);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"비디오 파일을 열 수 없습니다: {ex.Message}");
                    }
                }
            }
        }

        // 드래그 데이터 생성
        private Point _dragStartPoint;
        private void VideoList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragStartPoint = e.GetPosition(null);
        }

        private void VideoList_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                Point mousePos = e.GetPosition(null);
                Vector diff = _dragStartPoint - mousePos;

                if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                    Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
                {
                    ListBox listBox = sender as ListBox;
                    if (listBox == null) return;

                    var selectedVideo = listBox.SelectedItem as MyVideo;
                    if (selectedVideo != null)
                    {
                        DataObject dragData = new DataObject("MyVideo", selectedVideo);
                        DragDrop.DoDragDrop(listBox, dragData, DragDropEffects.Copy);
                    }
                }
            }
        }


        // 카메라 리스트에서 타임라인으로 Drag & Drop
        private async void Timeline_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent("MyVideo"))
            {
                var video = e.Data.GetData("MyVideo") as MyVideo;
                if (video != null)
                {
                    // 썸네일 생성
                    _mainViewModel.VideoEditor.GenerateThumbnails(video.FullPath);

                    // 영상 화면에 표시
                    var media = new Media(_libVLC, video.FullPath, FromType.FromPath);
                    await media.Parse(MediaParseOptions.ParseLocal); // 비동기 파싱, 필요시
                    _mediaPlayer.Media = media;
                    _mediaPlayer.Pause();
                    SetCurrentVideoPath(video.FullPath);


                    show_VideoBar.Visibility = Visibility.Visible;

                    // 타이머 시작
                    if (_timer == null)
                    {
                        _timer = new DispatcherTimer();
                        _timer.Interval = TimeSpan.FromMilliseconds(33);
                        _timer.Tick += Timer_Tick;
                    }
                    _timer.Stop();
                }
            }
        }

        // DragOver
        private void Timeline_DragOver(object sender, DragEventArgs e)
        {
            // "MyVideo" 타입의 데이터가 드래그 중이면 복사 가능 효과 표시
            if (e.Data.GetDataPresent("MyVideo"))
            {
                e.Effects = DragDropEffects.Copy;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true; // 이벤트가 처리됨을 명시
        }



        // 미디어가 로드된 후(오디오/비디오 길이 알 수 있음)
        private void MediaPlayer_LengthChanged(object sender, MediaPlayerLengthChangedEventArgs e)
        {
            // e.Length는 밀리초 단위(long)
            double totalSec = e.Length / 1000.0;

            // UI 스레드에서 실행 필요 (Dispatcher 사용)
            Dispatcher.Invoke(() =>
            {
                sliderSeekBar.Maximum = totalSec;
                txtTotalTime.Text = FormatTime(totalSec);
            });
        }

        // (3) 미디어가 끝까지 재생된 경우
        private void mediaElement_MediaEnded(object sender, RoutedEventArgs e)
        {
            // 자동 정지 + 위치 0으로 초기화
            StopPlayback();
        }

        // (4) 재생/일시정지 버튼
        private void btnPlayPause_Click(object sender, RoutedEventArgs e)
        {
            if (_mediaPlayer == null) return; // 파일이 없는 경우 무시

            if (_isPlaying)
            {
                _mediaPlayer.Pause();
                _isPlaying = false;
                btnPlayPause.Content = "▶";
                _timer.Stop();
            }
            else
            {
                _mediaPlayer.Play();
                _isPlaying = true;
                btnPlayPause.Content = "❚❚";
                _timer?.Start();
            }
        }

        // (5) 정지 버튼
        private void btnStop_Click(object sender, RoutedEventArgs e)
        {
            if (_mediaPlayer == null) return;
            StopPlayback();
        }

        // 정지 로직(미디어/캡처 위치를 0으로, 타이머 진행도 멈춤)
        private void StopPlayback()
        {
            _mediaPlayer.Stop();
            sliderSeekBar.Value = 0;
            txtCurrentTime.Text = "00:00:00";


            _isPlaying = false;
            btnPlayPause.Content = "▶";

            // 필요하다면 _timer는 계속 돌리거나, 멈출 수 있음
            // 여기서는 일단 계속 돌려서 업데이트 하도록 둠
            // _timer.Stop();
        }

        // (6) 타이머: 오디오 재생 위치에 맞춰 영상도 디코딩
        private void Timer_Tick(object sender, EventArgs e)
        {
            if (_mediaPlayer == null || !_isPlaying) return;
            if (_isSeeking) return; // 사용자가 슬라이더 조작 중이면 건너뜀

            double currentSec = _mediaPlayer.Time / 1000.0;
            sliderSeekBar.Value = currentSec;
            txtCurrentTime.Text = FormatTime(currentSec);
        }



        // (7) 슬라이더를 클릭했을 때, 해당 위치로 즉시 이동
        private bool _isSeeking = false;
        private void sliderSeekBar_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            _isSeeking = true;
        }

        private void sliderSeekBar_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            _isSeeking = false;
            if (_mediaPlayer != null)
                _mediaPlayer.Time = (long)(sliderSeekBar.Value * 1000);
        }


        // (8) 슬라이더 값이 바뀌면 -> 오디오/영상 위치도 이동
        private void sliderSeekBar_ValueChanged(object sender,
            System.Windows.RoutedPropertyChangedEventArgs<double> e)
        {
            if (_mediaPlayer == null) return;
            if (_isSeeking)
            {
                _mediaPlayer.Time = (long)(sliderSeekBar.Value * 1000);
                txtCurrentTime.Text = FormatTime(sliderSeekBar.Value);
            }
        }

        // (9) 볼륨 조절 (0~1)
        private void sliderVolume_ValueChanged(object sender,
            System.Windows.RoutedPropertyChangedEventArgs<double> e)
        {
            if (_mediaPlayer != null)
            {
                _mediaPlayer.Volume = (int)(sliderVolume.Value * 100);
            }
        }

        // 시간(초)을 "hh:mm:ss" 형태로 변환
        private string FormatTime(double totalSeconds)
        {
            TimeSpan ts = TimeSpan.FromSeconds(totalSeconds);
            return ts.ToString(@"hh\:mm\:ss");
        }

        // Mat -> BitmapSource 변환
        private BitmapSource ToBitmapSource(Mat mat)
        {
            using (var bitmap = mat.ToBitmap())
            {
                IntPtr hBitmap = bitmap.GetHbitmap();
                BitmapSource bitmapSource = Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap,
                    IntPtr.Zero,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                DeleteObject(hBitmap);
                return bitmapSource;
            }
        }

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteObject(IntPtr hObject);

        // 창 종료 시 자원 정리
        protected override void OnClosed(EventArgs e)
        {
            _timer?.Stop();
            _mediaPlayer?.Stop();
            _mediaPlayer?.Dispose();
            _libVLC?.Dispose();
            base.OnClosed(e);
        }

        private void OpenTrimWindow_Click(object sender, RoutedEventArgs e)
        {
            // 현재 비디오가 선택되지 않았다면, 현재 재생 중인 파일 경로를 설정
            if (string.IsNullOrEmpty(_currentVideoPath) && _mediaPlayer != null)
            {
                SetCurrentVideoPath(_mediaPlayer.Media.Mrl);
            }

            // 비디오 파일이 없으면 경고 메시지 출력
            if (string.IsNullOrEmpty(_currentVideoPath))
            {
                MessageBox.Show("먼저 영상을 선택하거나 재생해야 합니다.", "오류");
                return;
            }

            // TrimVideo.xaml 창 열기
            TrimVideoWindow trimWindow = new TrimVideoWindow();
            trimWindow.Owner = this;
            trimWindow.ShowDialog();
        }



        public async void TrimVideoFromUI(TimeSpan startTime, TimeSpan endTime)
        {
            if (string.IsNullOrEmpty(_currentVideoPath))
            {
                MessageBox.Show("먼저 영상을 선택해주세요.", "오류");
                return;
            }

            try
            {
                string outputFile = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(_currentVideoPath),
                    $"trimmed_{System.IO.Path.GetFileName(_currentVideoPath)}");

                CutVideoButton.IsEnabled = false;
                _cts = new CancellationTokenSource();
                var progress = new Progress<string>(s => StatusTextBlock.Text = s);

                await Task.Run(() => _editFunction.TrimVideo(_currentVideoPath, outputFile, startTime, endTime, progress, _cts.Token));

                MessageBox.Show("영상 자르기가 완료되었습니다!", "성공");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"오류: {ex.Message}", "오류");
            }
            finally
            {
                CutVideoButton.IsEnabled = true;
                _cts?.Dispose();
            }
        }

        //// 영상 잇는 함수
        //private async void ConcatenateVideoButton_Click(object sender, RoutedEventArgs e)
        //{
        //    if (string.IsNullOrEmpty(_currentVideoPath))
        //    {
        //        MessageBox.Show("먼저 첫 번째 영상을 선택해주세요.", "오류");
        //        return;
        //    }

        //    OpenFileDialog openFileDialog = new OpenFileDialog
        //    {
        //        Filter = "비디오 파일|*.mp4;*.avi;*.mov;*.wmv;*.mkv|모든 파일|*.*"
        //    };

        //    if (openFileDialog.ShowDialog() == true)
        //    {
        //        string secondVideoPath = openFileDialog.FileName;
        //        string outputFile = System.IO.Path.Combine(
        //            System.IO.Path.GetDirectoryName(_currentVideoPath),
        //            $"concatenated_{System.IO.Path.GetFileName(_currentVideoPath)}");

        //        try
        //        {
        //            ConcatenateVideoButton.IsEnabled = false;
        //            _cts = new CancellationTokenSource();
        //            var progress = new Progress<string>(s => StatusTextBlock.Text = s);

        //            await Task.Run(() => _editFunction.ConcatenateVideos(_currentVideoPath, secondVideoPath, outputFile, progress, _cts.Token));

        //            MessageBox.Show("영상 이어붙이기가 완료되었습니다!", "성공");
        //        }
        //        catch (OperationCanceledException)
        //        {
        //            MessageBox.Show("작업이 취소되었습니다.", "취소됨");
        //        }
        //        catch (Exception ex)
        //        {
        //            MessageBox.Show($"오류: {ex.Message}", "오류");
        //        }
        //        finally
        //        {
        //            ConcatenateVideoButton.IsEnabled = true;
        //            _cts?.Dispose();
        //        }
        //    }
        //}
    }
}
