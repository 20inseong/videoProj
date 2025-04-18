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

namespace WpfApp2
{
    public partial class MainWindow : Window
    {
        private VideoCapture _capture;
        private DispatcherTimer _timer;
        private bool _isPlaying = false; // 재생 상태 파악 (Play/Pause 구분)
        private MainViewModel _mainViewModel;
        private EditFunction _editFunction = new EditFunction();
        private string _currentVideoPath;
        private CancellationTokenSource _cts;
        //private MyVideoViewModel _videoViewModel;

        public MainWindow()
        {
            InitializeComponent();

            _mainViewModel = new MainViewModel();
            this.DataContext = _mainViewModel;
            this.StateChanged += Window_StateChanged;
            //_videoViewModel = new MyVideoViewModel();
            //this.DataContext = _videoViewModel;
        }

        // taskbar제외 화면 길이 구하기
        private void Window_StateChanged(object sender, EventArgs e)
        {
            if (WindowState == WindowState.Maximized)
            {
                MaxHeight = SystemParameters.WorkArea.Height +1;
                MaxWidth = SystemParameters.WorkArea.Width +1;
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

        //// 영상 바꾸는 함수
        //private void VideoList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        //{
        //    if (e.AddedItems.Count > 0)
        //    {
        //        MyVideo selectedVideo = (MyVideo)e.AddedItems[0];

        //        if (File.Exists(selectedVideo.FullPath))
        //        {
        //            // 현재 선택된 비디오 경로를 _currentVideoPath에 저장
        //            SetCurrentVideoPath(selectedVideo.FullPath);

        //            // 기존 VideoCapture 객체 해제
        //            _capture?.Dispose();
        //            _capture = new VideoCapture(selectedVideo.FullPath, VideoCapture.API.Any);

        //            // 기존 타이머 멈추기 (이전 영상의 잔여 타이머 제거)
        //            _timer?.Stop();

        //            // MediaElement의 영상 변경
        //            mediaElement.Source = new Uri(selectedVideo.FullPath);
        //            mediaElement.Position = TimeSpan.Zero; // 첫 장면으로 이동
        //            mediaElement.Pause();
        //            _isPlaying = false;
        //            btnPlayPause.Content = "▶"; // 재생 버튼 초기화

        //            // 새로운 비디오의 섬네일 생성 🔥
        //            _mainViewModel.VideoEditor.GenerateThumbnails(selectedVideo.FullPath);

        //            // 영상 길이 가져와서 슬라이더 값 업데이트
        //            if (_capture != null)
        //            {
        //                double fps = _capture.Get(CapProp.Fps);
        //                double totalFrames = _capture.Get(CapProp.FrameCount);
        //                double videoLength = totalFrames / fps; // 총 길이(초)

        //                sliderSeekBar.Value = 0; // 재생 위치 초기화
        //                sliderSeekBar.Maximum = videoLength; // 슬라이더 최대 길이 업데이트
        //                txtCurrentTime.Text = "00:00:00"; // 현재 시간 초기화
        //                txtTotalTime.Text = FormatTime(videoLength); // 총 길이 표시
        //            }

        //            // 새로운 비디오의 첫 번째 프레임 표시
        //            using (Mat frame = new Mat())
        //            {
        //                _capture.Read(frame);
        //                if (!frame.IsEmpty)
        //                {
        //                    imgDisplay.Source = ToBitmapSource(frame);
        //                }
        //            }

        //            // 타이머 다시 시작
        //            _timer?.Start();
        //        }
        //        else
        //        {
        //            MessageBox.Show("선택한 파일을 찾을 수 없습니다.");
        //        }
        //    }
        //}



        //private void sliderTimeline_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        //{
        //    if (mediaElement.Source != null)
        //    {
        //        mediaElement.Position = TimeSpan.FromSeconds(e.NewValue);
        //    }
        //}

        // 섬네일 클릭 시, 해당 장면으로 전환
        //private void Thumbnail_Click(object sender, MouseButtonEventArgs e)
        //{
        //    Image clickedImage = sender as Image;
        //    if (clickedImage == null) return;

        //    // 클릭한 썸네일의 DataContext를 가져오기
        //    ThumbnailItem selectedThumbnail = clickedImage.DataContext as ThumbnailItem;
        //    if (selectedThumbnail == null) return;

        //    // 선택한 시간으로 비디오 이동
        //    mediaElement.Position = TimeSpan.FromSeconds(selectedThumbnail.TimePosition);

        //    mediaElement.Pause();

        //}



        // 영상 재생과 관련된 함수들
        // 영상 선택 버튼
        private void btnSelectVideo_Click(object sender, RoutedEventArgs e)
        {

            OpenFileDialog openFileDialog = new OpenFileDialog
            {
                Filter = "비디오 파일|*.mp4;*.avi;*.mov;*.wmv;*.mkv|모든 파일|*.*",
                Multiselect = true  // 여러 개의 파일 선택 가능
            };

            if (openFileDialog.ShowDialog() == true)
            {
                //// 기존 자원 해제
                //_capture?.Dispose();

                foreach (string videoPath in openFileDialog.FileNames) // 여러 개의 파일 처리
                {
                    try
                    {
                        //// Emgu CV로 영상 디코딩
                        //_capture = new VideoCapture(openFileDialog.FileName, VideoCapture.API.Any);

                        //// MediaElement로 오디오 재생
                        //mediaElement.Source = new Uri(openFileDialog.FileName);
                        //mediaElement.Volume = sliderVolume.Value; // 볼륨 설정

                        //try
                        //{
                        // 파일명 추출
                        string fileName = System.IO.Path.GetFileName(openFileDialog.FileName);
                        //_mainViewModel.VideoEditor.GenerateThumbnails(videoPath);

                        //    // 파일명이 유효한지 확인
                        //    if (string.IsNullOrEmpty(fileName))
                        //    {
                        //        MessageBox.Show("파일을 선택하지 않았습니다.");
                        //        //txtFileName.Text = "Why so serious?";
                        //        return;
                        //    }

                        //    // ViewModel이 null인지 확인
                        //    if (_mainViewModel.VideoList == null)
                        //    {
                        //        _mainViewModel.VideoList = new MyVideoViewModel(); // 초기화
                        //    }

                        _mainViewModel.VideoList.AddVideo(fileName, videoPath);
                        //}
                        //catch (Exception ex)
                        //{
                        //    // 예외 처리 (로그 기록 또는 사용자 알림)
                        //    MessageBox.Show($"오류가 발생했습니다: {ex.Message}");
                        //    //txtFileName.Text = "Error is coming";
                        //}

                        //// 타이머 (약 30fps)
                        //if (_timer == null)
                        //{
                        //    _timer = new DispatcherTimer();
                        //    _timer.Interval = TimeSpan.FromMilliseconds(33);
                        //    _timer.Tick += Timer_Tick;
                        //}

                        //// 비디오 작동바 가시화
                        //show_VideoBar.Visibility = Visibility.Visible;

                        //// 재생 시작(수정 -> 첫 장면에서 일시정지)
                        //mediaElement.Position = TimeSpan.Zero; // 첫 프레임으로 이동
                        //mediaElement.Pause();
                        //_isPlaying = false;
                        //btnPlayPause.Content = "▶"; // 재생 아이콘
                        //_timer.Start();

                        //// 첫 번째 프레임을 imgDisplay에 표시
                        //using (Mat frame = new Mat())
                        //{
                        //    _capture.Read(frame);
                        //    if (!frame.IsEmpty)
                        //    {
                        //        imgDisplay.Source = ToBitmapSource(frame);
                        //    }
                        //}
                        //SetCurrentVideoPath(videoPath);
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
        private void Timeline_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent("MyVideo"))
            {
                var video = e.Data.GetData("MyVideo") as MyVideo;
                if (video != null)
                {
                    // 썸네일 생성
                    _mainViewModel.VideoEditor.GenerateThumbnails(video.FullPath);

                    // 영상 화면에 표시
                    _capture?.Dispose();
                    _capture = new VideoCapture(video.FullPath, VideoCapture.API.Any);
                    mediaElement.Source = new Uri(video.FullPath);
                    mediaElement.Position = TimeSpan.Zero;
                    mediaElement.Pause();
                    using (Mat frame = new Mat())
                    {
                        _capture.Read(frame);
                        if (!frame.IsEmpty)
                        {
                            imgDisplay.Source = ToBitmapSource(frame);
                        }
                    }
                    SetCurrentVideoPath(video.FullPath);

                    show_VideoBar.Visibility = Visibility.Visible;

                    // 타이머 시작
                    if (_timer == null)
                    {
                        _timer = new DispatcherTimer();
                        _timer.Interval = TimeSpan.FromMilliseconds(33);
                        _timer.Tick += Timer_Tick;
                    }
                    _timer.Start();
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
        private void mediaElement_MediaOpened(object sender, RoutedEventArgs e)
        {
            if (mediaElement.NaturalDuration.HasTimeSpan)
            {
                double totalSec = mediaElement.NaturalDuration.TimeSpan.TotalSeconds;
                sliderSeekBar.Maximum = totalSec;
                txtTotalTime.Text = FormatTime(totalSec);
            }
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
            if (mediaElement.Source == null) return; // 파일이 없는 경우 무시

            if (_isPlaying)
            {
                // 현재 재생 중이면 -> 일시정지
                mediaElement.Pause();
                _isPlaying = false;
                btnPlayPause.Content = "▶";
            }
            else
            {
                // 일시정지 상태면 -> 재생
                mediaElement.Play();
                _isPlaying = true;
                btnPlayPause.Content = "❚❚";
            }
        }

        // (5) 정지 버튼
        private void btnStop_Click(object sender, RoutedEventArgs e)
        {
            if (mediaElement.Source == null) return;
            StopPlayback();
        }

        // 정지 로직(미디어/캡처 위치를 0으로, 타이머 진행도 멈춤)
        private void StopPlayback()
        {
            mediaElement.Stop();
            mediaElement.Position = TimeSpan.Zero;
            sliderSeekBar.Value = 0;
            txtCurrentTime.Text = "00:00:00";

            _capture?.Set(CapProp.PosMsec, 0);
            imgDisplay.Source = null;

            _isPlaying = false;
            btnPlayPause.Content = "▶";

            // 필요하다면 _timer는 계속 돌리거나, 멈출 수 있음
            // 여기서는 일단 계속 돌려서 업데이트 하도록 둠
            // _timer.Stop();
        }

        // (6) 타이머: 오디오 재생 위치에 맞춰 영상도 디코딩
        private void Timer_Tick(object sender, EventArgs e)
        {
            if (_capture == null || !_isPlaying) return;

            double currentSec = mediaElement.Position.TotalSeconds;
            sliderSeekBar.Value = currentSec;
            txtCurrentTime.Text = FormatTime(currentSec);

            // 영상도 동일 시각으로
            _capture.Set(CapProp.PosMsec, currentSec * 1000.0);

            using (Mat frame = new Mat())
            {
                _capture.Read(frame);
                if (!frame.IsEmpty)
                {
                    imgDisplay.Source = ToBitmapSource(frame);
                }
            }
        }

        // (7) 슬라이더를 클릭했을 때, 해당 위치로 즉시 이동
        private void sliderSeekBar_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            var slider = (System.Windows.Controls.Slider)sender;
            var clickPoint = e.GetPosition(slider);

            double ratio = clickPoint.X / slider.ActualWidth;
            if (ratio < 0) ratio = 0;
            if (ratio > 1) ratio = 1;

            double newValue = slider.Minimum + (slider.Maximum - slider.Minimum) * ratio;
            slider.Value = newValue;

            // 기본 드래그 동작 방지
            e.Handled = true;
        }

        // (8) 슬라이더 값이 바뀌면 -> 오디오/영상 위치도 이동
        private void sliderSeekBar_ValueChanged(object sender,
            System.Windows.RoutedPropertyChangedEventArgs<double> e)
        {
            if (mediaElement.Source == null) return;
            if (!_isPlaying) return; // 재생 중일 때만 동기화

            mediaElement.Position = TimeSpan.FromSeconds(sliderSeekBar.Value);
            _capture?.Set(CapProp.PosMsec, sliderSeekBar.Value * 1000.0);

            //// 진행 표시(PlaybackIndicator) 위치 업데이트
            //if (mediaElement.NaturalDuration.HasTimeSpan)
            //{
            //    double progressRatio = sliderSeekBar.Value / sliderSeekBar.Maximum;
            //    double timelineWidth = sliderSeekBar.ActualWidth;

            //    double newX = progressRatio * timelineWidth;
            //    PlaybackIndicator.RenderTransform = new TranslateTransform(newX, 0);
            //}
        }

        // (9) 볼륨 조절 (0~1)
        private void sliderVolume_ValueChanged(object sender,
            System.Windows.RoutedPropertyChangedEventArgs<double> e)
        {
            if (mediaElement != null)
            {
                mediaElement.Volume = sliderVolume.Value;
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
            _capture?.Dispose();
            mediaElement?.Stop();
            mediaElement.Source = null;
            base.OnClosed(e);
        }

        private void OpenTrimWindow_Click(object sender, RoutedEventArgs e)
        {
            // 현재 비디오가 선택되지 않았다면, 현재 재생 중인 파일 경로를 설정
            if (string.IsNullOrEmpty(_currentVideoPath) && mediaElement.Source != null)
            {
                SetCurrentVideoPath(mediaElement.Source.LocalPath);
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


        // 영상 잇는 함수
        private async void ConcatenateVideoButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentVideoPath))
            {
                MessageBox.Show("먼저 첫 번째 영상을 선택해주세요.", "오류");
                return;
            }

            OpenFileDialog openFileDialog = new OpenFileDialog
            {
                Filter = "비디오 파일|*.mp4;*.avi;*.mov;*.wmv;*.mkv|모든 파일|*.*"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                string secondVideoPath = openFileDialog.FileName;
                string outputFile = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(_currentVideoPath),
                    $"concatenated_{System.IO.Path.GetFileName(_currentVideoPath)}");

                try
                {
                    ConcatenateVideoButton.IsEnabled = false;
                    _cts = new CancellationTokenSource();
                    var progress = new Progress<string>(s => StatusTextBlock.Text = s);

                    await Task.Run(() => _editFunction.ConcatenateVideos(_currentVideoPath, secondVideoPath, outputFile, progress, _cts.Token));

                    MessageBox.Show("영상 이어붙이기가 완료되었습니다!", "성공");
                }
                catch (OperationCanceledException)
                {
                    MessageBox.Show("작업이 취소되었습니다.", "취소됨");
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"오류: {ex.Message}", "오류");
                }
                finally
                {
                    ConcatenateVideoButton.IsEnabled = true;
                    _cts?.Dispose();
                }
            }
        }


    }
}
